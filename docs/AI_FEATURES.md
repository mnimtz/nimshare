# AI feature ideas for NimShare

Below are **eight concrete AI use-cases** we could add to NimShare, ordered from
"quick win + high user value" to "ambitious + big lift". Each entry lists what
the user would see, what infrastructure it needs, and which providers make sense.

Design principle: **AI is opt-in per user**. NimShare must keep working when the
tenant hasn't configured any AI provider — features degrade silently.

## Provider strategy

Add an **AI Gateway** settings page (`/settings/ai`) alongside the Email Gateway,
same pattern:

| Provider | Best for | API cost (rough) | Notes |
|---|---|---|---|
| **OpenAI** (GPT-4o mini) | Cheap+good all-rounder, structured JSON | ~$0.15 / 1M input tokens | Best-in-class tool calling |
| **Google Gemini** (2.0 Flash / 1.5 Flash) | Multi-modal (image, PDF, audio), long context | Free tier + $0.075 / 1M | Native PDF/image ingestion |
| **Anthropic Claude** (Haiku / Sonnet) | Long text, careful reasoning, low hallucination | Haiku ~$0.25 / 1M | Best structured output |
| **Azure OpenAI** | Enterprise Marcus's tenant already talks to | Same as OpenAI | GDPR-compliant EU endpoints |

Wire it as an abstraction (`IAiProvider`) with three implementations (OpenAI,
Gemini, Anthropic). One admin picks the provider + puts in an API key,
encrypted via DataProtection just like the email gateway.

---

## 1. Auto-summary + smart preview for shared files ⭐ high value / low lift

**User story:** As a recipient landing on `/s/{slug}` for a PDF I've never
seen, I want a **2-3 sentence AI summary** at the top before I click Download,
so I know if it's what I actually need.

- **Trigger:** on demand — a "Generate preview" button on the recipient's
  landing page, or automatic for files ≤ 20 MB.
- **Pipeline:** blob → text extraction (PDF: PdfPig; Word: OpenXml; slides:
  same) → LLM prompt ("Summarize in 3 sentences") → cache on the file for 7
  days.
- **Providers:** Gemini 2.0 Flash (native PDF ingest, cheapest), or Claude
  Haiku (best structured summary).
- **Privacy note:** show a small "AI summary — the recipient triggered this"
  chip so the file owner sees who requested it.

## 2. Smart tags / auto-classification ⭐ high value

**User story:** Uploader drops "Vertrag_Kunde_Q3.pdf" → NimShare auto-tags it
"contract", "customer", "German", "Q3-2026" and picks Personal vs Group scope
based on similar past uploads.

- **Trigger:** background, right after `POST /files/{id}/complete`.
- **Pipeline:** filename + first 1 KB of extracted text → LLM classify
  against the user's own tag history + a small taxonomy.
- **UI:** tags shown in the file list, filterable in the search box.
- **Providers:** Any small model — GPT-4o mini is plenty at ~0.01 ct / file.

## 3. Natural-language file search ⭐⭐ killer feature

**User story:** In the search box at the top of `/files/personal`, type
*"the offer I sent Aldi last spring"* → NimShare finds it, even though the
file is called `AA_off_v3-final.docx`.

- **Pipeline:** embed all file names + first 2 KB of content on ingest
  (OpenAI `text-embedding-3-small`, ~0.01 ct / file). Store in
  `pgvector` on Azure Postgres or in SQL Server 2025's new vector column,
  or in a lightweight [SQLite VSS](https://github.com/asg017/sqlite-vss)
  extension for the current Sqlite backend.
- **Query time:** encode the query → cosine-search top 20.
- **Providers:** OpenAI embeddings (simplest). Alternatively local
  BGE-small via ONNX runtime = zero recurring cost.

## 4. Reverse-share upload requests: AI-guided form ⭐ high UX win

**User story:** Marcus creates an upload-request link for "quarterly
compliance report from each subsidiary". Every recipient sees the same link,
but AI generates a **personalized cover message** based on the recipient's
email domain (Aldi employee → German tone; Richemont → French).

- **Pipeline:** on link creation, Marcus writes ONE English prompt template
  → LLM personalizes per recipient email at delivery time.
- **Providers:** Claude Haiku (best tone-shifting), or Gemini Flash.

## 5. Content risk detection ⭐ compliance win

**User story:** Someone uploads to the **Public** bucket → NimShare
scans and flags files that look like credit card data, GDPR PII, medical
records, or credentials. Uploader sees a soft warning; admins see it in a
new "Flagged files" screen.

- **Pipeline:** first 2 KB of text → LLM classify against a small policy list
  → append `RiskFlag` field on the StorageFile.
- **Complements** Azure Defender for Storage (which finds *malware*, not
  content-classification issues).
- **Providers:** Gemini (has native "SafetySettings" built in) or Claude
  (best refusal quality).

## 6. AI-drafted share emails ⭐ nice polish

**User story:** In the "Send by email" dialog, click **Draft with AI** → an
LLM composes a proper cover email tailored to the file type + the sender's
past email tone.

- **Pipeline:** file metadata + prior sent emails (opt-in) → LLM draft →
  user edits → send.
- **Providers:** GPT-4o mini or Claude Haiku (both cheap).

## 7. "Chat with your files" ⭐⭐ ambitious / big lift

**User story:** In the Personal or Group scope, a **Chat** tab: "Which of
my 400 uploaded PDFs mentions '§ 3 DSGVO' with a fine over €10k?" —
NimShare does retrieval-augmented Q&A across the user's files.

- **Requires:** embeddings (see #3) + a chunker + a chat controller.
- **Providers:** OpenAI GPT-4o for retrieval-augmented answers,
  Claude Sonnet for careful legal answers.
- **Cost:** meaningful at scale — needs quota per user.

## 8. Automatic OCR + language detection ⭐ table stakes for scanned docs

**User story:** Uploader drops `scan-42.pdf` → NimShare detects it's a scan,
runs OCR, indexes the text so it participates in search (#3), summary (#1)
and tags (#2). Language of the scan is detected so downstream LLM prompts
respond in the same language.

- **Providers:** Tesseract for free/offline; Azure AI Document
  Intelligence for handwritten + tables; Google Gemini's own OCR is
  surprisingly good and free-tier friendly.

---

## Suggested rollout order

1. **AI Gateway settings page** — abstraction + one working provider (OpenAI).
2. **Feature #1** (auto-summary on `/s/{slug}` landing) — quick win, high WOW.
3. **Feature #2** (smart tags) — background, invisible until visible.
4. **Feature #3** (semantic search) — real user-value driver.
5. **Feature #5** (content risk) — Admin/compliance appeal.
6. **Feature #7** (chat) — after the above land.

## Data-protection stance we should commit to

- **No file bytes leave the tenant without an explicit user action** (Feature
  #1 needs the *recipient* to click "Generate preview"; Features #2, #3, #5
  need Admin to opt-in per group/bucket and are surfaced in a badge on the
  file card).
- **All AI calls are logged** with the model, prompt, and file id (not the
  content) — visible under a per-user "AI usage" page.
- **Prompts include a fixed system message** forbidding the model from
  sharing content with any tool call the tenant hasn't authorized.
- **EU-first**: default to Azure OpenAI Sweden Central or Google's EU-only
  endpoint if the user picks GDPR-strict.

## Mobile-app compatibility

Every AI endpoint is a JSON `POST /api/v1/ai/…` — the exact same shape as
files/links. iOS/Android apps hit them with a JWT bearer, get the summary
JSON, render in native widgets. No web-only surface anywhere.
