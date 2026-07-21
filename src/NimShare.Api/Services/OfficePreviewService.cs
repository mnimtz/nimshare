using System.Diagnostics;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.70: Server-seitige DOCX/XLSX/PPTX/ODT → PDF Konvertierung mittels
/// LibreOffice-headless. Ergebnis wird im Blob-Storage unter
/// `office-cache/{fileId}.pdf` gecacht, damit wiederholte Previews ohne
/// erneute Konvertierung auskommen.
///
/// Design-Prinzipien:
///  - Concurrency: max 2 parallele soffice-Prozesse (SemaphoreSlim). Ein
///    soffice-Prozess kann bei einem großen XLSX 30-60 s brauchen und
///    verbraucht ~200 MB RAM — mehr parallel würde einen 1-GB-App-Service
///    ins OOM treiben.
///  - Timeout: 60 s pro Konvertierung. Kein User wartet 5 Minuten auf ein
///    kaputtes Dokument.
///  - Cache-Key: NUR fileId. Wenn der User die File-Version wechselt,
///    muss die Cache-Datei explizit invalidiert werden (TODO wenn File-
///    Versionen re-uploadable werden).
/// </summary>
public interface IOfficePreviewService
{
    bool IsSupported(string contentType);
    /// <summary>Returns a read-SAS URL pointing to the cached PDF (creates it on-demand).</summary>
    Task<Uri?> GetPreviewUrlAsync(Guid fileId, string sourceBlobPath, string sourceName,
        string sourceContentType, CancellationToken ct = default);
}

public class OfficePreviewService : IOfficePreviewService
{
    private readonly IBlobStorageService _blobs;
    private readonly ILogger<OfficePreviewService> _log;
    // Concurrency-Bremse: mehr als 2 soffice-Prozesse parallel würden
    // einen typischen Azure App Service (1-2 GB RAM) in OOM treiben.
    private static readonly SemaphoreSlim _slot = new(2, 2);

    private const string CachePrefix = "office-cache/";
    private const int TimeoutSeconds = 60;

    // Content-Types die LibreOffice zuverlässig zu PDF konvertieren kann.
    // OpenDocument-Formate + MS Office (ab 2007) + Rich Text + Legacy.
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", // .docx
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",       // .xlsx
        "application/vnd.openxmlformats-officedocument.presentationml.presentation", // .pptx
        "application/vnd.oasis.opendocument.text",     // .odt
        "application/vnd.oasis.opendocument.spreadsheet", // .ods
        "application/vnd.oasis.opendocument.presentation", // .odp
        "application/msword",     // .doc (legacy)
        "application/vnd.ms-excel",       // .xls (legacy)
        "application/vnd.ms-powerpoint",  // .ppt (legacy)
        "application/rtf",                 // .rtf
        "text/rtf",
    };

    public OfficePreviewService(IBlobStorageService blobs, ILogger<OfficePreviewService> log)
    {
        _blobs = blobs;
        _log = log;
    }

    public bool IsSupported(string contentType) =>
        !string.IsNullOrEmpty(contentType) && Supported.Contains(contentType);

    public async Task<Uri?> GetPreviewUrlAsync(Guid fileId, string sourceBlobPath, string sourceName,
        string sourceContentType, CancellationToken ct = default)
    {
        var cachePath = $"{CachePrefix}{fileId:N}.pdf";
        // Cache-Hit? Direkt eine SAS liefern.
        if (await _blobs.ExistsAsync(cachePath, ct))
            return _blobs.CreateInlineSas(cachePath, "application/pdf", TimeSpan.FromMinutes(10));

        // Konvertieren — hinter Slot-Semaphore damit nicht 10 User
        // gleichzeitig soffice starten.
        await _slot.WaitAsync(ct);
        try
        {
            // Zweite Cache-Check: es könnte sein dass ein anderer
            // Request in der Warteschlange dasselbe File konvertiert
            // hat während wir gewartet haben.
            if (await _blobs.ExistsAsync(cachePath, ct))
                return _blobs.CreateInlineSas(cachePath, "application/pdf", TimeSpan.FromMinutes(10));

            var workDir = Path.Combine(Path.GetTempPath(), "nimshare-office", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            var srcFilename = SafeFilename(sourceName);
            var srcPath = Path.Combine(workDir, srcFilename);
            try
            {
                // 1. Blob → lokale Datei
                await using (var fs = File.Create(srcPath))
                    await _blobs.DownloadToAsync(sourceBlobPath, fs, ct);

                // 2. soffice --headless --convert-to pdf --outdir workDir srcPath
                //    Läuft in einem eigenen User-Profile-Ordner um race
                //    conditions bei parallelen Aufrufen zu vermeiden
                //    (LibreOffice sperrt sonst das Default-Profil).
                var userProfile = Path.Combine(workDir, "profile");
                Directory.CreateDirectory(userProfile);
                var psi = new ProcessStartInfo("soffice")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = workDir,
                };
                psi.ArgumentList.Add("--headless");
                psi.ArgumentList.Add("--nologo");
                psi.ArgumentList.Add("--nofirststartwizard");
                psi.ArgumentList.Add($"-env:UserInstallation=file://{userProfile}");
                psi.ArgumentList.Add("--convert-to");
                psi.ArgumentList.Add("pdf");
                psi.ArgumentList.Add("--outdir");
                psi.ArgumentList.Add(workDir);
                psi.ArgumentList.Add(srcPath);
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("soffice startete nicht");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                try { await proc.WaitForExitAsync(timeoutCts.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    _log.LogWarning("soffice timeout nach {S}s für File {FileId} ({Name})",
                        TimeoutSeconds, fileId, sourceName);
                    return null;
                }
                if (proc.ExitCode != 0)
                {
                    var stderr = await proc.StandardError.ReadToEndAsync(ct);
                    _log.LogWarning("soffice ExitCode={Code} für File {FileId} ({Name}). Stderr: {Err}",
                        proc.ExitCode, fileId, sourceName, stderr);
                    return null;
                }

                // 3. Ergebnis-PDF finden — LibreOffice benennt es nach
                //    Basename(src).pdf, in workDir.
                var pdfName = Path.GetFileNameWithoutExtension(srcFilename) + ".pdf";
                var pdfPath = Path.Combine(workDir, pdfName);
                if (!File.Exists(pdfPath))
                {
                    _log.LogWarning("soffice OK, aber kein PDF gefunden bei {Path}", pdfPath);
                    return null;
                }

                // 4. Upload zum Cache-Blob
                await using (var pdfStream = File.OpenRead(pdfPath))
                    await _blobs.UploadFromStreamAsync(cachePath, pdfStream, "application/pdf", ct);

                return _blobs.CreateInlineSas(cachePath, "application/pdf", TimeSpan.FromMinutes(10));
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
            }
        }
        finally { _slot.Release(); }
    }

    private static string SafeFilename(string name)
    {
        // LibreOffice mag keine Unicode-Steuerzeichen oder Path-Separator.
        // Wir haben eh nur den Base-Name — die Extension muss aber stimmen
        // damit soffice den richtigen Import-Filter wählt.
        var safe = new string(name.Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(safe)) safe = "document";
        return safe;
    }
}
