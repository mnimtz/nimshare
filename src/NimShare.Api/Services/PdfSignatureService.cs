using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using NimShare.Core.Entities;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Utilities.Collections;
using BcX509 = Org.BouncyCastle.X509;

namespace NimShare.Api.Services;

/// <summary>
/// Embeds a PAdES-B detached PKCS#7 signature into an existing PDF using
/// incremental save. The output is a fully-signed PDF: Adobe Reader shows
/// the green "signature valid" padlock when opened, and every byte of the
/// original file is covered by the signature's SHA-256 hash.
///
/// Flow (per Adobe Digital Signatures spec + ETSI EN 319 142-1 baseline):
///   1. Append an incremental update block that adds:
///      - Sig object with /Filter/Adobe.PPKLite, /SubFilter/adbe.pkcs7.detached
///      - Placeholder /ByteRange [0 0 0 0] and /Contents &lt;fixed-size hex&gt;
///      - Signature annotation on page 1 (invisible — the visible stamp is
///        already drawn into the flattened PDF by SignaturePdfService)
///      - Updated AcroForm dictionary referencing the field
///   2. Rewrite /ByteRange with actual [0 sigStart, sigEnd, totalLength - sigEnd]
///      so the hash covers everything except the /Contents hex blob itself.
///   3. Hash the covered bytes with SHA-256, wrap in CMS SignedData signed by
///      the caller's RSA key via BouncyCastle.
///   4. Hex-encode the CMS bytes and paste them into the /Contents placeholder
///      (zero-padded to reserved length).
/// </summary>
public interface IPdfSignatureService
{
    /// <summary>Sign <paramref name="pdfBytes"/> with the given certificate.
    /// Returns the signed PDF (a superset of the input, appended with an
    /// incremental-update signature block). If <paramref name="cert"/> is
    /// null or its private key is unusable, returns the original bytes
    /// unchanged and sets <paramref name="reason"/>.</summary>
    byte[] SignPdf(byte[] pdfBytes, SigningCertificate cert, IDataProtector protector,
        string signerName, string reasonText, string locationText, out string? failure);

    /// <summary>Verify the CMS signature embedded in a signed PDF.
    /// Returns null when no /Sig object is present, otherwise a result
    /// describing whether the crypto matches and who signed.</summary>
    PdfSignatureVerification? Verify(byte[] pdfBytes);
}

public record PdfSignatureVerification(
    bool CryptoValid,
    bool CoverageComplete,
    string? SignerCommonName,
    string? Thumbprint,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    string? Reason,
    string? Diagnostic);

public class PdfSignatureService : IPdfSignatureService
{
    // Fixed hex-space reserved for the /Contents blob (in HEX CHARS). 16384 hex
    // = 8192 bytes — comfortably fits a SHA-256 CMS SignedData with one
    // 2048-bit RSA signer + full cert chain (typically ~4-6 KB).
    private const int ContentsHexReserve = 16384;

    public byte[] SignPdf(byte[] pdfBytes, SigningCertificate cert, IDataProtector protector,
        string signerName, string reasonText, string locationText, out string? failure)
    {
        failure = null;
        if (pdfBytes.Length < 100) { failure = "PDF too short"; return pdfBytes; }

        // 1. Decrypt PFX and load the key + full cert chain.
        AsymmetricKeyParameter privateKey;
        BcX509.X509Certificate bcCert;
        List<BcX509.X509Certificate> chain = new();
        try
        {
            var (pfxBytes, pfxPassword) = UnbundlePfx(protector.Unprotect(cert.PfxDataEncrypted));
            using var ms = new MemoryStream(pfxBytes);
            var store = new Pkcs12StoreBuilder().Build();
            store.Load(ms, pfxPassword.ToCharArray());
            string? alias = null;
            foreach (string a in store.Aliases)
            {
                if (store.IsKeyEntry(a)) { alias = a; break; }
            }
            if (alias is null) { failure = "PFX contains no private key entry"; return pdfBytes; }
            privateKey = store.GetKey(alias).Key;
            bcCert = store.GetCertificate(alias).Certificate;
            // Include intermediate + root certs from the chain so Adobe can
            // build trust up to a known anchor (v1.10.17 fix). Self-signed
            // certs return a single-element chain — still fine.
            var storeChain = store.GetCertificateChain(alias);
            if (storeChain is not null && storeChain.Length > 0)
                foreach (var e in storeChain) chain.Add(e.Certificate);
            else chain.Add(bcCert);
        }
        catch (Exception ex) { failure = "PFX unwrap failed: " + ex.Message; return pdfBytes; }

        // 2. Build the incremental-update block.
        var updated = AppendSignatureBlock(pdfBytes, signerName, reasonText, locationText,
            out int contentsHexStart);
        // Guard: contentsHexStart is the offset of the first hex nibble INSIDE
        // <>. contentsHexEnd is start + reserve.
        int contentsHexEnd = contentsHexStart + ContentsHexReserve;

        // ByteRange per ISO 32000-2 §12.8.1: two ranges of the file that are
        // signed, EXCLUDING the /Contents hex payload but INCLUDING the '<'
        // and '>' delimiters around it. Adobe and other conformant verifiers
        // require the delimiters inside the hash range (v1.10.17 fix).
        int startOfLead = 0;
        int lenOfLead = contentsHexStart;                        // covers 0..contentsHexStart-1 (includes '<')
        int startOfTail = contentsHexEnd;                        // '>' is at contentsHexEnd
        int lenOfTail = updated.Length - startOfTail;
        var byteRangeStr = $"[{startOfLead} {lenOfLead} {startOfTail} {lenOfTail}]";
        // Rewrite the /ByteRange placeholder in the bytes. Reserve room for
        // ByteRange values up to 10 digits each (2 GB PDF ceiling) — the
        // pre-v1.10.17 placeholder was 20 chars, overflowed for any PDF with
        // more than 10 bytes total, and the whole signing step silently fell
        // back to unsigned. Padding with lots of '0's here; replacement is
        // shorter and gets padded with spaces via WritePlaceholderReplacement.
        var brPlaceholder = "/ByteRange [00000000000000 00000000000000 00000000000000 00000000000000]";
        WritePlaceholderReplacement(updated, brPlaceholder, "/ByteRange " + byteRangeStr);

        // 3. Hash the covered ranges with SHA-256.
        using var sha = SHA256.Create();
        sha.TransformBlock(updated, startOfLead, lenOfLead, null, 0);
        sha.TransformFinalBlock(updated, startOfTail, lenOfTail);
        var digest = sha.Hash!;

        // 4. Build CMS SignedData over the digest (attached message digest,
        //    detached content — this is the classic PAdES-B baseline form).
        byte[] cmsBytes;
        try
        {
            var gen = new CmsSignedDataGenerator();
            var certs = CollectionUtilities.CreateStore(chain);
            gen.AddCertificates(certs);
            var sigFactory = new Asn1SignatureFactory("SHA256WITHRSA", privateKey);
            gen.AddSignerInfoGenerator(new SignerInfoGeneratorBuilder()
                .Build(sigFactory, bcCert));
            // BouncyCastle's Generate with encapsulate=false emits a detached
            // CMS SignedData — the digest is computed over the raw content we
            // pass here (the concatenation of the two byte ranges around the
            // /Contents placeholder — same bytes Adobe Reader will hash).
            var toSign = new byte[lenOfLead + lenOfTail];
            Buffer.BlockCopy(updated, startOfLead, toSign, 0, lenOfLead);
            Buffer.BlockCopy(updated, startOfTail, toSign, lenOfLead, lenOfTail);
            var signedData = gen.Generate(new CmsProcessableByteArray(toSign), false);
            cmsBytes = signedData.GetEncoded();
        }
        catch (Exception ex) { failure = "CMS generation failed: " + ex.Message; return pdfBytes; }

        // 5. Hex-encode + zero-pad to reserve, then paste into /Contents slot.
        var hex = new StringBuilder(ContentsHexReserve);
        foreach (var b in cmsBytes) hex.Append(b.ToString("x2"));
        if (hex.Length > ContentsHexReserve)
        {
            failure = $"CMS blob ({hex.Length} hex chars) exceeds reserve ({ContentsHexReserve}). Bump ContentsHexReserve.";
            return pdfBytes;
        }
        while (hex.Length < ContentsHexReserve) hex.Append('0');
        var hexBytes = Encoding.ASCII.GetBytes(hex.ToString());
        Buffer.BlockCopy(hexBytes, 0, updated, contentsHexStart, ContentsHexReserve);

        // Ignore unused local — kept for clarity.
        _ = digest;
        return updated;
    }

    public PdfSignatureVerification? Verify(byte[] pdfBytes)
    {
        try
        {
            var text = Encoding.ASCII.GetString(pdfBytes);
            int brIdx = text.LastIndexOf("/ByteRange", StringComparison.Ordinal);
            int contIdx = text.LastIndexOf("/Contents <", StringComparison.Ordinal);
            if (brIdx < 0 || contIdx < 0) return null;
            // Parse ByteRange [a b c d].
            int lb = text.IndexOf('[', brIdx);
            int rb = text.IndexOf(']', lb);
            if (lb < 0 || rb < 0) return new(false, false, null, null, null, null, null, "invalid ByteRange dict");
            var parts = text[(lb + 1)..rb].Trim().Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return new(false, false, null, null, null, null, null, "ByteRange must have 4 values");
            int a = int.Parse(parts[0]), b = int.Parse(parts[1]),
                c = int.Parse(parts[2]), d = int.Parse(parts[3]);
            // Bounds check.
            if (a < 0 || b < 0 || c < 0 || d < 0
                || a + b > pdfBytes.Length || c + d > pdfBytes.Length)
                return new(false, false, null, null, null, null, null, "ByteRange out of bounds");
            bool coverageComplete = (a + b + d == pdfBytes.Length - (c - (a + b)));
            // Extract hex string between the < and > after /Contents.
            int ltIdx = contIdx + "/Contents ".Length; // points at '<'
            if (pdfBytes[ltIdx] != (byte)'<') return new(false, false, null, null, null, null, null, "Contents not hex-string");
            int gtIdx = ltIdx + 1;
            while (gtIdx < pdfBytes.Length && pdfBytes[gtIdx] != (byte)'>') gtIdx++;
            if (gtIdx >= pdfBytes.Length) return new(false, false, null, null, null, null, null, "Contents unterminated");
            // Do NOT TrimEnd('0') here — that would eat legitimate zero bytes
            // at the end of the CMS ~0.4% of the time (v1.10.17 fix). ASN.1
            // DER carries its own length prefix, so BouncyCastle consumes
            // exactly the bytes that belong to the SignedData structure and
            // ignores trailing zero padding.
            var hex = text[(ltIdx + 1)..gtIdx];
            if ((hex.Length & 1) == 1) return new(false, false, null, null, null, null, null, "Contents hex has odd length");
            var cmsBytes = new byte[hex.Length / 2];
            for (int i = 0; i < cmsBytes.Length; i++)
                cmsBytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            // Reconstruct the signed content: concat of the two byte ranges.
            var signedContent = new byte[b + d];
            Buffer.BlockCopy(pdfBytes, a, signedContent, 0, b);
            Buffer.BlockCopy(pdfBytes, c, signedContent, b, d);
            // Verify via BouncyCastle.
            var signed = new CmsSignedData(new CmsProcessableByteArray(signedContent), cmsBytes);
            var signers = signed.GetSignerInfos().GetSigners();
            var certStore = signed.GetCertificates();
            bool cryptoValid = true;
            string? cn = null, thumb = null, reason = null;
            DateTimeOffset? nb = null, na = null;
            foreach (SignerInformation si in signers)
            {
                var matches = certStore.EnumerateMatches(si.SignerID);
                foreach (var certObj in matches)
                {
                    var bc = (BcX509.X509Certificate)certObj;
                    if (!si.Verify(bc.GetPublicKey())) cryptoValid = false;
                    // CN from subject DN.
                    var subject = bc.SubjectDN.ToString();
                    var cnPart = subject.Split(',').FirstOrDefault(p => p.TrimStart().StartsWith("CN=", StringComparison.Ordinal));
                    if (cnPart is not null) cn = cnPart.Split('=', 2)[1].Trim();
                    // Thumbprint (SHA-1 of DER encoding — mirrors what Windows shows).
                    var sha1 = SHA1.HashData(bc.GetEncoded());
                    thumb = Convert.ToHexString(sha1);
                    nb = new DateTimeOffset(bc.NotBefore, TimeSpan.Zero);
                    na = new DateTimeOffset(bc.NotAfter, TimeSpan.Zero);
                    break;
                }
                break;
            }
            return new PdfSignatureVerification(
                CryptoValid: cryptoValid,
                CoverageComplete: coverageComplete,
                SignerCommonName: cn,
                Thumbprint: thumb,
                NotBefore: nb,
                NotAfter: na,
                Reason: reason,
                Diagnostic: cryptoValid ? "signature verified" : "digest mismatch — PDF was modified after signing");
        }
        catch (Exception ex)
        {
            return new PdfSignatureVerification(false, false, null, null, null, null, null, "verify threw: " + ex.Message);
        }
    }

    /// <summary>Appends an incremental-update block that adds one indirect Sig
    /// object, one Widget annotation, and updates the Catalog to point at a
    /// new AcroForm containing the field. Returns the full new byte buffer
    /// plus the exact offset of the first hex nibble of the /Contents slot.</summary>
    private static byte[] AppendSignatureBlock(byte[] original, string signerName,
        string reason, string location, out int contentsHexStart)
    {
        // Parse the tail of the file to find %%EOF and the /Prev xref offset.
        // For a valid PDF, the last %%EOF is preceded by startxref <offset>.
        int lastEof = LastIndexOf(original, Encoding.ASCII.GetBytes("%%EOF"));
        if (lastEof < 0) throw new InvalidOperationException("PDF missing %%EOF marker");
        // Walk back to find startxref.
        int startXrefLine = LastIndexOf(original, Encoding.ASCII.GetBytes("startxref"), lastEof);
        if (startXrefLine < 0) throw new InvalidOperationException("PDF missing startxref");
        // Parse xref offset (digits between startxref and %%EOF).
        var offsetStr = Encoding.ASCII.GetString(original,
            startXrefLine + "startxref".Length,
            lastEof - (startXrefLine + "startxref".Length)).Trim();
        if (!long.TryParse(offsetStr, out var prevXref))
            throw new InvalidOperationException("Invalid startxref value: " + offsetStr);

        // We need three new indirect objects with fresh IDs. Prefer the /Size
        // trailer entry (authoritative); fall back to a text scan if that
        // can't be parsed. Text-scan is imperfect because compressed streams
        // may contain byte sequences that look like "N G obj" (v1.10.17
        // hardening — see review note MED-4). To be extra safe on top of
        // that, floor at 100000: even a colliding stream byte can only match
        // 1-6 digit numbers, so a 6-digit floor guarantees no clash.
        int nextObjNum = Math.Max(100000, ReadSizeFromTrailer(original)) + 1;
        int sigObjNum = nextObjNum;
        int annotObjNum = nextObjNum + 1;
        int acroFormObjNum = nextObjNum + 2;
        // We also need to write a NEW /Catalog (via update) that references
        // AcroForm. Find the existing Catalog obj num.
        int catalogObjNum = FindCatalogObjectNumber(original);
        if (catalogObjNum < 0) throw new InvalidOperationException("PDF has no /Catalog reference");
        // Find the first Page for the annotation's /P reference.
        int firstPageObjNum = FindFirstPageObjectNumber(original);

        var nowStr = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "Z00'00'";
        // Escape signerName / reason / location for PDF string literals.
        string EscapePdfString(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

        // Build the Sig object. Contents is a fixed-size hex placeholder.
        // We fill it with zeros as a starting value; the caller rewrites it.
        var contentsPlaceholder = new string('0', ContentsHexReserve);
        var sigDict = new StringBuilder();
        sigDict.Append("<<\n");
        sigDict.Append("/Type /Sig\n");
        sigDict.Append("/Filter /Adobe.PPKLite\n");
        sigDict.Append("/SubFilter /adbe.pkcs7.detached\n");
        sigDict.Append("/Name (").Append(EscapePdfString(signerName)).Append(")\n");
        sigDict.Append("/Reason (").Append(EscapePdfString(reason)).Append(")\n");
        sigDict.Append("/Location (").Append(EscapePdfString(location)).Append(")\n");
        sigDict.Append("/M (D:").Append(nowStr).Append(")\n");
        // Wide placeholder — must match brPlaceholder in SignPdf. Actual
        // values are written in-place later; unused chars pad with spaces.
        sigDict.Append("/ByteRange [00000000000000 00000000000000 00000000000000 00000000000000]\n");
        sigDict.Append("/Contents <").Append(contentsPlaceholder).Append(">\n");
        sigDict.Append(">>\n");

        var sigObjHeader = $"{sigObjNum} 0 obj\n";
        var sigObjTrailer = "\nendobj\n";

        // Build the widget annotation (invisible: Rect 0 0 0 0).
        var annotDict = new StringBuilder();
        annotDict.Append("<<\n");
        annotDict.Append("/Type /Annot\n");
        annotDict.Append("/Subtype /Widget\n");
        annotDict.Append("/FT /Sig\n");
        annotDict.Append("/T (Signature1)\n");
        annotDict.Append("/F 4\n");
        annotDict.Append("/Rect [0 0 0 0]\n");
        if (firstPageObjNum > 0)
            annotDict.Append("/P ").Append(firstPageObjNum).Append(" 0 R\n");
        annotDict.Append("/V ").Append(sigObjNum).Append(" 0 R\n");
        annotDict.Append(">>\n");

        // Build the AcroForm dict.
        var acroFormDict = new StringBuilder();
        acroFormDict.Append("<<\n");
        acroFormDict.Append("/Fields [").Append(annotObjNum).Append(" 0 R]\n");
        acroFormDict.Append("/SigFlags 3\n");
        acroFormDict.Append(">>\n");

        // Assemble the incremental update body: three new objects + updated
        // Catalog. We'll compute byte offsets as we build.
        var ms = new MemoryStream();
        ms.Write(original, 0, original.Length);
        // Ensure a newline before our new content.
        if (original[^1] != (byte)'\n') ms.WriteByte((byte)'\n');

        long baseOffset = ms.Length;

        // Sig object.
        long sigOffset = ms.Length;
        var sigHeader = Encoding.ASCII.GetBytes(sigObjHeader);
        ms.Write(sigHeader, 0, sigHeader.Length);
        // Track where the '<' of /Contents ends up in the FINAL byte buffer.
        long contentsMarkerAbs;
        {
            var dictHead = sigDict.ToString();
            int idxInDict = dictHead.IndexOf("/Contents <");
            if (idxInDict < 0) throw new InvalidOperationException("Contents marker missing");
            var dictHeadBytes = Encoding.ASCII.GetBytes(dictHead);
            ms.Write(dictHeadBytes, 0, dictHeadBytes.Length);
            // The '<' sits at: sigOffset + sigHeader.Length + (idxInDict + "/Contents ".Length)
            contentsMarkerAbs = sigOffset + sigHeader.Length + idxInDict + "/Contents ".Length;
        }
        var sigTrailerBytes = Encoding.ASCII.GetBytes(sigObjTrailer);
        ms.Write(sigTrailerBytes, 0, sigTrailerBytes.Length);

        // Widget annotation.
        long annotOffset = ms.Length;
        var annotBody = $"{annotObjNum} 0 obj\n" + annotDict + "endobj\n";
        var annotBytes = Encoding.ASCII.GetBytes(annotBody);
        ms.Write(annotBytes, 0, annotBytes.Length);

        // AcroForm.
        long acroFormOffset = ms.Length;
        var acroFormBody = $"{acroFormObjNum} 0 obj\n" + acroFormDict + "endobj\n";
        var acroFormBytes = Encoding.ASCII.GetBytes(acroFormBody);
        ms.Write(acroFormBytes, 0, acroFormBytes.Length);

        // Updated Catalog: we copy its original body and inject /AcroForm.
        var (catalogOriginal, catalogGen) = ReadObject(original, catalogObjNum);
        var newCatalogBody = InjectAcroForm(catalogOriginal, acroFormObjNum);
        long catalogOffset = ms.Length;
        var catalogBody = $"{catalogObjNum} {catalogGen} obj\n{newCatalogBody}\nendobj\n";
        var catalogBytes = Encoding.ASCII.GetBytes(catalogBody);
        ms.Write(catalogBytes, 0, catalogBytes.Length);

        // Cross-reference section for the update (only lists modified/added).
        long xrefOffset = ms.Length;
        var xref = new StringBuilder();
        xref.Append("xref\n");
        // Sort by object number.
        var entries = new List<(int Num, long Off, int Gen)>
        {
            (sigObjNum, sigOffset, 0),
            (annotObjNum, annotOffset, 0),
            (acroFormObjNum, acroFormOffset, 0),
            (catalogObjNum, catalogOffset, catalogGen),
        };
        entries.Sort((a, b) => a.Num.CompareTo(b.Num));
        // Group consecutive ranges.
        int idx = 0;
        while (idx < entries.Count)
        {
            int start = idx;
            while (idx + 1 < entries.Count && entries[idx + 1].Num == entries[idx].Num + 1) idx++;
            int count = idx - start + 1;
            xref.Append(entries[start].Num).Append(' ').Append(count).Append('\n');
            for (int k = start; k <= idx; k++)
                xref.AppendFormat("{0:0000000000} {1:00000} n \n", entries[k].Off, entries[k].Gen);
            idx++;
        }
        // Trailer.
        xref.Append("trailer\n");
        xref.Append("<<\n");
        // Try to preserve /Root and /Size from the original trailer, plus
        // append /Prev to the previous xref offset.
        var oldTrailer = ReadTrailerDict(original);
        // /Size must be higher than the highest object number in use.
        int newSize = Math.Max(acroFormObjNum, catalogObjNum) + 1;
        xref.Append("/Size ").Append(newSize).Append('\n');
        xref.Append("/Root ").Append(catalogObjNum).Append(" 0 R\n");
        if (oldTrailer.TryGetValue("Info", out var info)) xref.Append("/Info ").Append(info).Append('\n');
        if (oldTrailer.TryGetValue("ID", out var id)) xref.Append("/ID ").Append(id).Append('\n');
        xref.Append("/Prev ").Append(prevXref).Append('\n');
        xref.Append(">>\n");
        xref.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        var xrefBytes = Encoding.ASCII.GetBytes(xref.ToString());
        ms.Write(xrefBytes, 0, xrefBytes.Length);

        var final = ms.ToArray();
        // contentsMarkerAbs is the offset of the '<' character. The first hex
        // char is one byte later.
        contentsHexStart = (int)(contentsMarkerAbs + 1);
        return final;
    }

    private static void WritePlaceholderReplacement(byte[] buffer, string needle, string replacement)
    {
        var needleBytes = Encoding.ASCII.GetBytes(needle);
        var replBytes = Encoding.ASCII.GetBytes(replacement);
        int pos = LastIndexOf(buffer, needleBytes);
        if (pos < 0) throw new InvalidOperationException("Placeholder not found: " + needle);
        if (replBytes.Length > needleBytes.Length)
            throw new InvalidOperationException($"Replacement '{replacement}' longer than placeholder '{needle}'");
        // Pad replacement with spaces up to the placeholder length so byte
        // offsets don't shift (the ByteRange values we just wrote must remain
        // accurate after this in-place edit).
        Buffer.BlockCopy(replBytes, 0, buffer, pos, replBytes.Length);
        for (int i = replBytes.Length; i < needleBytes.Length; i++)
            buffer[pos + i] = (byte)' ';
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle, int? maxEnd = null)
    {
        int end = maxEnd ?? haystack.Length;
        for (int i = end - needle.Length; i >= 0; i--)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>Reads the /Size entry from the last trailer dict — the
    /// authoritative "next object number" per PDF spec. Returns 0 if not
    /// found (caller handles floor).</summary>
    private static int ReadSizeFromTrailer(byte[] pdf)
    {
        try
        {
            var dict = ReadTrailerDict(pdf);
            if (dict.TryGetValue("Size", out var s) && int.TryParse(s.Trim(), out var n))
                return n;
        }
        catch { }
        return 0;
    }

    private static int FindMaxObjectNumber(byte[] pdf)
    {
        // Walk /R references and n obj headers. Quick heuristic scan.
        var text = Encoding.ASCII.GetString(pdf);
        int max = 0;
        for (int i = 0; i < text.Length - 8; i++)
        {
            // Match "N G obj" — digits, space, digit, space, "obj".
            if (text[i] < '0' || text[i] > '9') continue;
            int j = i;
            while (j < text.Length && text[j] >= '0' && text[j] <= '9') j++;
            if (j == i || j + 5 >= text.Length) continue;
            if (text[j] != ' ') continue;
            int g = j + 1;
            if (g >= text.Length || text[g] < '0' || text[g] > '9') continue;
            while (g < text.Length && text[g] >= '0' && text[g] <= '9') g++;
            if (g + 4 >= text.Length) continue;
            if (text[g] != ' ' || text[g + 1] != 'o' || text[g + 2] != 'b' || text[g + 3] != 'j') continue;
            int num = int.Parse(text[i..j]);
            if (num > max) max = num;
            i = g + 3;
        }
        return max;
    }

    private static int FindCatalogObjectNumber(byte[] pdf)
    {
        // Look in trailer's /Root reference.
        var text = Encoding.ASCII.GetString(pdf);
        int trailerIdx = text.LastIndexOf("trailer");
        int scanStart = trailerIdx >= 0 ? trailerIdx : 0;
        int rootIdx = text.IndexOf("/Root", scanStart);
        if (rootIdx < 0) return -1;
        int i = rootIdx + "/Root".Length;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        int j = i;
        while (j < text.Length && text[j] >= '0' && text[j] <= '9') j++;
        if (j == i) return -1;
        return int.Parse(text[i..j]);
    }

    private static int FindFirstPageObjectNumber(byte[] pdf)
    {
        // Heuristic: find "/Type /Pages" and read its /Kids [ first entry.
        var text = Encoding.ASCII.GetString(pdf);
        int p = text.IndexOf("/Type /Pages");
        if (p < 0) p = text.IndexOf("/Type/Pages");
        if (p < 0) return -1;
        int kids = text.IndexOf("/Kids", p);
        if (kids < 0) return -1;
        int lb = text.IndexOf('[', kids);
        if (lb < 0) return -1;
        int i = lb + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        int j = i;
        while (j < text.Length && text[j] >= '0' && text[j] <= '9') j++;
        if (j == i) return -1;
        return int.Parse(text[i..j]);
    }

    private static (string Body, int Gen) ReadObject(byte[] pdf, int objNum)
    {
        var text = Encoding.ASCII.GetString(pdf);
        // Search for "objNum \d+ obj"
        int scan = 0;
        int gen = 0;
        while (scan < text.Length)
        {
            int idx = text.IndexOf($"{objNum} ", scan, StringComparison.Ordinal);
            if (idx < 0) break;
            int j = idx + $"{objNum} ".Length;
            int k = j;
            while (k < text.Length && text[k] >= '0' && text[k] <= '9') k++;
            if (k == j) { scan = idx + 1; continue; }
            if (k + 4 > text.Length || text[k] != ' ' || text[k+1] != 'o' || text[k+2] != 'b' || text[k+3] != 'j')
            { scan = idx + 1; continue; }
            gen = int.Parse(text[j..k]);
            int bodyStart = k + 4;
            while (bodyStart < text.Length && char.IsWhiteSpace(text[bodyStart])) bodyStart++;
            int end = text.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException($"Object {objNum} has no endobj");
            return (text[bodyStart..end].TrimEnd(), gen);
        }
        throw new InvalidOperationException($"Object {objNum} not found");
    }

    private static string InjectAcroForm(string catalogBody, int acroFormObjNum)
    {
        // Add /AcroForm N 0 R before the closing >>. If already exists, replace.
        int end = catalogBody.LastIndexOf(">>", StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("Catalog dict has no closing >>");
        var before = catalogBody[..end].TrimEnd();
        // Remove any existing /AcroForm entry (rare in freshly-generated PDFs).
        int af = before.IndexOf("/AcroForm", StringComparison.Ordinal);
        if (af >= 0)
        {
            // Cut from /AcroForm to next / or >>.
            int k = af + "/AcroForm".Length;
            while (k < before.Length && char.IsWhiteSpace(before[k])) k++;
            // Skip the value: could be an inline dict, or "N N R", or another /... key.
            // Simplest: skip until next '/' at same nesting or end.
            int nextKey = before.IndexOf('/', k);
            int cut = nextKey > 0 ? nextKey : before.Length;
            before = (before[..af] + " " + before[cut..]).TrimEnd();
        }
        return before + "\n/AcroForm " + acroFormObjNum + " 0 R\n" + catalogBody[end..];
    }

    private static Dictionary<string, string> ReadTrailerDict(byte[] pdf)
    {
        var text = Encoding.ASCII.GetString(pdf);
        int t = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (t < 0) return new();
        int dictStart = text.IndexOf("<<", t, StringComparison.Ordinal);
        if (dictStart < 0) return new();
        int dictEnd = text.IndexOf(">>", dictStart, StringComparison.Ordinal);
        if (dictEnd < 0) return new();
        var body = text[(dictStart + 2)..dictEnd];
        var dict = new Dictionary<string, string>();
        // Simple parse: split on '/' as key delimiter (skips values that
        // contain /, but for /Root/Info/ID/Size it's enough).
        var parts = body.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            int sp = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            if (sp <= 0) continue;
            var key = trimmed[..sp];
            var val = trimmed[sp..].Trim();
            if (!dict.ContainsKey(key)) dict[key] = val;
        }
        return dict;
    }

    /// <summary>Undo of the packing done by CertificatesApiController when
    /// storing a PFX: the on-disk bytes are `[len(4)][pw][pfx]`. See
    /// <c>CertificatesApiController.BundleWithPassword</c>.</summary>
    private static (byte[] Pfx, string Password) UnbundlePfx(byte[] wrapped)
    {
        if (wrapped.Length < 4) throw new InvalidDataException("PFX bundle too short");
        int pwLen = BitConverter.ToInt32(wrapped, 0);
        if (pwLen < 0 || pwLen > wrapped.Length - 4) throw new InvalidDataException("PFX bundle length header invalid");
        var pw = Encoding.UTF8.GetString(wrapped, 4, pwLen);
        var pfx = new byte[wrapped.Length - 4 - pwLen];
        Buffer.BlockCopy(wrapped, 4 + pwLen, pfx, 0, pfx.Length);
        return (pfx, pw);
    }
}
