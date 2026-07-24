# NimShare

**Self-hosted, brand-styled file sharing on Azure — with signatures, AI, and a native iOS app.** Upload once, share via personalised links with expiry, password, download limits, custom domains, QR codes and file-request (upload) links. Multi-user with fine-grained permissions, native iOS app, EFIGS+NL localised, and AI features that actually help (chat with your files, smart tags, semantic search, humorous greetings).

Built on **ASP.NET Core 8 + Azure App Service + Azure Blob Storage**. Deploy in one click.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fmnimtz%2Fnimshare%2Fmain%2Finfra%2Fazuredeploy.json)
[![Visualize](https://raw.githubusercontent.com/Azure/azure-quickstart-templates/master/1-CONTRIBUTION-GUIDE/images/visualizebutton.svg)](http://armviz.io/#/?load=https%3A%2F%2Fraw.githubusercontent.com%2Fmnimtz%2Fnimshare%2Fmain%2Finfra%2Fazuredeploy.json)

![Tungsten Automation](https://img.shields.io/badge/brand-Tungsten%20Automation-002854?style=flat)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat)
![iOS 17+](https://img.shields.io/badge/iOS-17%2B-000000?style=flat&logo=apple)
![License: MIT](https://img.shields.io/badge/license-MIT-brightgreen?style=flat)
![i18n: EFIGS + NL](https://img.shields.io/badge/i18n-EFIGS%20%2B%20NL-00A0FB?style=flat)

---

## What's inside

NimShare grew from a link-sharing MVP into a full file-collaboration platform. Highlights, grouped by area.

### Sharing links (the original superpower)
- **Custom slugs** — `nimshare.com/s/project-x` instead of random tokens.
- **Password protection** — bcrypt-hashed, verified per request.
- **Expiration** — absolute date/time or "burn after N downloads".
- **Download limits** — hard cap per link, atomically counted.
- **Recipient allowlist** — restrict a public link to specific email addresses.
- **Optional email-gate + one-time-passcode** before download.
- **Multiple links per file** — different rules for different audiences.
- **Branded landing page** with optional message, preview, and AI auto-summary.
- **QR code** auto-generated for every link.
- **One-click revoke** without deleting the file.
- **Access statistics** per link — hits, downloads, referrers, country + city, device type, IP hashes.
- **Public links** admin-curated, visible to all users under **Meine Links**.

### Reverse-share (upload requests)
- Generate a public link that lets **someone else send you a file** — no account on their side.
- Same guard rails: expiry, max uploads, password.
- **AI-drafted cover message** personalised per recipient.
- **Recurring upload requests** for regular collections (monthly reports etc.).

### File browser (Seafile-style)
- Three scopes: **Personal**, **Public** (visible to all authenticated users), and **Group** (per-team).
- Full folder hierarchy with breadcrumbs, tree panel, drag-and-drop upload, bulk selection.
- **Grid vs. list view**, right-click context menu, hover actions.
- **Inline preview** for images and PDFs; **Office preview** via LibreOffice-headless for .docx/.xlsx/.pptx.
- **Precise file-format icons** (PDF, DOCX, PNG, MP4, ZIP, …) — same design on web and iOS.
- **Folder ZIP download** — streams recursively as a proper file (temp-file, fault-tolerant per blob).
- **Bulk ZIP** for selected files, or single-file "as ZIP".
- **Move / Copy / Rename / Delete** for both files and folders, with target-folder picker.
- **Favourites**, **Trash / Restore**, **Recent activity feed**, **Pinned items**.
- **Full-text search** across files and folder names.
- **File versioning** — every replace keeps the previous version, restorable.
- **File locking** + **OnlyOffice** integration for concurrent editing.

### Permissions ("Windows-ACL for public folders")
- A public folder can be marked **private** — only creator, admin, and explicitly-granted users see it.
- **Direct-shares** grant read/write to individual users or entire groups.
- **Sub-folder permission overrides** (e.g. cap child folder to read-only even when parent is write).
- **Auto-privatize**: setting a group/user permission on a public folder automatically switches it to private — so "share with group X" actually means "restrict to X", not "additive".
- **🔒 badge** on private folders, permissions modal via right-click.

### Digital signatures (DocuSign-alike)
- Draw signatures with the mouse (web) or **Apple Pencil** (iOS, via PencilKit).
- Visual field placement on the PDF with pdf.js + drag-drop.
- **Sequential delivery** with per-recipient deadlines and auto-reminders.
- **Bring-your-own certificate** (PKCS#12 upload) for cryptographic signing, or platform-managed certificates.
- **Signature audit log** with forensic fields (country, city, device, timezone, IP hash).
- Full **REST API** for signature requests; native iOS Requester + Signer flows.

### AI features (opt-in, provider-agnostic)
Bring your own key for **Gemini, OpenAI or Anthropic**, or leave the AI Gateway disabled.
- **Chat with your files** — RAG over per-file embeddings, cosine-search on the fly.
- **Auto-summary** on public share landing pages.
- **Smart tags** — auto-classify uploads on completion.
- **Semantic search** across your library.
- **Vision-AI for images** via `DescribeImageAsync`.
- **OCR + language detection** for scanned PDFs.
- **AI-drafted share emails** and **upload-request cover messages** personalised per recipient.
- **Content risk detection** on public uploads (flags problematic files for review).
- **AI-drafted email templates** for signature invites.
- **Home-screen greeting** — witty, localised (EFIGS+NL), with today's weather (Open-Meteo, no API key) and optional cloud-status summary from a configured [incident.io](https://incident.io/) status page.
- **Reindex button** to rebuild embeddings after schema changes.

### Contacts, notifications, bookmarks
- **Address book** with import from CSV.
- **Notifications tray** with badge count; per-event settings.
- **Bookmarks** — company-wide curated link collection (admin-managed), with emoji-picker; useful for pinning internal tools and portals.

### Native iOS app (SwiftUI, iOS 17+)
- **Adaptive one-screen home** — tiles auto-size to fill the viewport; personalised greeting + weather live in the greeting card.
- Full file browser with long-press context menu (share, permissions, move, copy, rename, delete, upload-request, sub-folder).
- **Signatures**: request creation, visual field placement, and signing via **PencilKit**.
- Favourites, Trash, Activity, Shared-with-me, Bookmarks — parity with the web UI.
- Custom server URL configurable per user; default `https://nimshare.com`.
- Localised in de/en/fr/it/es/nl; explicit `Accept-Language` so the KI greeting always matches the app UI.
- Two-factor sign-in (TOTP) supported end-to-end.

### Admin tools
- **User management** — invite via email, disable/enable, change role, delete, per-user quota, "last login" column.
- **Backup & Restore** — full DB export (JSON, all 35 entity types, topological FK ordering, IDENTITY-safe on SQL Server), one-click download, upload+restore with typed "RESTORE" confirmation. Self-lockout protection: aborts before wipe if the acting admin isn't in the incoming backup.
- **Email gateway** — SMTP or Resend.
- **AI gateway** — provider, model, temperature, status-page URL + product filter.
- **Office gateway** — OnlyOffice URL.
- **Signature certificates** — upload PKCS#12, view details.
- **API tokens** (scoped) and **webhooks** for integrations.
- **Landing templates** (global admin default + per-user override).
- **Content moderation** — flagged uploads inbox.
- **Blocked users** and **user-reporting** workflow.

### Auth & multi-tenancy
- **Local email + password**, always available. First user gets the Admin role via first-run setup wizard.
- **Microsoft Entra ID** optional, layered on top. Existing local accounts auto-link by email on first Entra sign-in.
- **Two-factor auth (TOTP)** with QR-code setup.
- **Session cookies** for web, **JWT bearer** for API/iOS — every `/api/v1/*` endpoint accepts both.
- **Roles**: `User` and `Administrator`.
- Safety rails prevent demoting/deleting the last active admin.

### Custom domains
- Add `share.your-company.com` to your account under **Settings → Domains**.
- Verify via TXT record (checked via Google DoH, one-click "Verify" button with real result).
- Azure App Service Managed Certificate is issued automatically once DNS is aligned.
- Configure a canonical **`App__PublicBaseUrl`** so generated links always use your custom host, regardless of which host the request came in on.

### Localisation — **EFIGS + NL** by default
Every user-facing string ships in **English, French, Italian, German, Spanish, and Dutch** from day one. Public share landings respect the visitor's `Accept-Language`; the app's own UI has a language switcher that persists per user. AI-generated content (summaries, chat responses, greetings) is produced in the caller's language via ISO code.

### Tungsten-branded UI
Follows the [Tungsten Automation Brand Book](https://tungstenautomation.com/): primary navy `#002854`, accent blue `#00A0FB`, yellow CTAs `#FFC600`, Red Hat Display, blue-domain gradient sidebar. Consistent look-and-feel with sibling internal projects like [printix-tonerwatch](https://github.com/mnimtz/printix-tonerwatch).

---

## Architecture at a glance

```
                        ┌──────────────────────────┐
    Web · iOS · Public  │       NimShare API       │
    link visitors       │  ASP.NET Core 8          │
   ────────────────────►│  MVC + Web API + Razor   │
                        └────────────┬─────────────┘
                                     │  1) Auth (cookie or JWT)
                                     │  2) Access rules (scope, DirectShare, IsPrivate)
                                     │  3) Link rules (pw, expiry, count, allowlist, revoked)
                                     │  4) Issue SAS (≤ 60 s TTL)
                                     ▼
                       ┌──────────────────────────────┐
   Direct ────────►    │  Azure Blob Storage           │
   upload/download     │  files/users/{uid}/{fid}/...  │
                       └──────────────────────────────┘

    ┌──────────────────┐   ┌──────────────────┐   ┌────────────────┐
    │  SQLite (default)│   │  App Insights +  │   │  AI Provider   │
    │  or Azure SQL    │   │  Log Analytics   │   │  (Gemini/OpenAI│
    │  (metadata)      │   │                  │   │   /Anthropic)  │
    └──────────────────┘   └──────────────────┘   └────────────────┘
```

- **Provider-agnostic EF Core** — same code path against SQLite (Azure Files mount) or Azure SQL. Handwritten migrations for both providers.
- **API-first**: every feature has an `/api/v1/*` JSON twin; the iOS app and the Razor MVC UI both use the same endpoints.
- **Streamed uploads/downloads** direct browser/app ↔ Blob Storage via short-lived SAS — mobile data plans aren't burned tunneling through the server.
- **Custom-domain middleware** looks up the incoming host to pick per-tenant branding for share landings.

Deep-dive in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and [`docs/CONCEPT.md`](docs/CONCEPT.md).

---

## Deploy

### 1-click on Azure

Click the **Deploy to Azure** button at the top. Only one parameter is required:

| Parameter | Default | Meaning |
|---|---|---|
| `siteName` | *(required)* | Globally unique App Service name → `https://<siteName>.azurewebsites.net` |
| `sku` | `B1` | Plan SKU. `B1` ≈ 10 EUR/month, always-on. `F1` free but sleeps. `S1`+ needed for free managed TLS on custom domains. |
| `location` | resource-group region | Azure region |
| `containerImage` | `ghcr.io/mnimtz/nimshare:latest` | Or pin a specific tag for reproducibility |
| `tz` | `Europe/Berlin` | IANA timezone for link timestamps |
| `defaultLang` | `en` | Fallback UI language (`en`/`de`/`fr`/`it`/`es`/`nl`) |

What the template provisions:
- **Linux App Service** running the container from GHCR (`ghcr.io/mnimtz/nimshare`)
- **Storage Account** with a `files` Blob container for user uploads and a `nimshare-data` File share mounted at `/data` for the SQLite metadata DB
- **Managed identity** with `Storage Blob Data Contributor` — no connection strings, no account keys in app settings

**No SQL Database, no Key Vault, no Entra ID parameters up front.** The app boots and the welcome page renders immediately.

### First run

Open your site → NimShare shows a **First-run setup wizard**. Enter your email, name and password → that account is the first Administrator, you're signed in and land on the dashboard.

### Optional: Entra ID sign-in on top

For "Sign in with Microsoft" alongside the local login, set two App Service application settings:

- `AzureAd__TenantId` — your tenant GUID (or `common` for multi-tenant)
- `AzureAd__ClientId` — the app registration's Application (client) ID

Restart the App Service. The login page then shows both.

### Optional: canonical link host

If you serve NimShare on `nimshare.com` but Azure keeps the `*.azurewebsites.net` host active as a fallback, set an app setting so every generated link uses your domain regardless of which host the request came in on:

```
App__PublicBaseUrl = https://nimshare.com
```

### Optional: connectors (OneDrive Business import)

Since v1.10.163, users can connect an external cloud storage and import folders/files directly into their NimShare Personal area (cloud-to-cloud streaming, no local download). Currently supported: **OneDrive Business** via Microsoft Graph.

1. Register the app in Entra ID: <https://entra.microsoft.com> → App registrations → New → Web client. Redirect URI: `https://YOUR-HOST/settings/connectors/onedrive/callback`.
2. Delegated API permissions: `Files.Read` + `User.Read` + `offline_access`. Grant admin consent for your tenant if you want tenant-wide.
3. Create a Client Secret and copy its value.
4. In Azure App Service → Configuration:
   ```
   Connectors__OneDrive__ClientId = <client-id>
   Connectors__OneDrive__ClientSecret = <secret-value>
   Connectors__OneDrive__Tenant = common     # or a specific tenant GUID
   ```
5. Restart. Users see `🔌 Connectors` in the sidebar and can add a OneDrive connection.

Without those settings the app runs normally; only the "Connect OneDrive" button responds with a config-missing error.

### iOS app

The iOS app is a SwiftUI project under [`ios/`](ios/). It's distributed via the App Store (bundle `email.nimtz.nimshare`). To build locally:

```bash
cd ios
xcodegen generate   # regenerates NimShare.xcodeproj from project.yml
open NimShare.xcodeproj
```

The default server URL is `https://nimshare.com`; each user can change it in **Profil → Server ändern**.

### Cost estimate

- App Service `B1`: ~10 EUR/month
- Storage account (100 GB Blob + 5 GB File share): ~2 EUR/month
- Optional Azure SQL Basic: ~5 EUR/month (recommended for heavier workloads)
- **Total: ~12–17 EUR/month** at personal-use volumes

See [`docs/COSTS.md`](docs/COSTS.md).

### GHCR image visibility

First deploy: make the container package public at <https://github.com/users/mnimtz/packages/container/nimshare/settings> → *Change visibility* → *Public*. Without this, App Service can't pull the image.

### Local development

```bash
git clone https://github.com/mnimtz/nimshare.git
cd nimshare
~/.dotnet/dotnet restore   # SDK 8.0.423 required; check `dotnet --list-sdks`
~/.dotnet/dotnet run --project src/NimShare.Api
```

Open <http://localhost:5099>. Uploads use an Azurite local Blob emulator:

```bash
docker run -d --name azurite -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite
```

For pure UI/CSS work, the public share landing (`/s/{slug}`) and the welcome page render without auth.

---

## API

Full OpenAPI spec is served at `/swagger` in Development, JSON at `/openapi/v1.json` in any environment. Quick reference in [`docs/API.md`](docs/API.md).

**All `/api/v1/*` endpoints accept both** the web session cookie AND a JWT bearer token — the iOS app uses the same endpoints as the web UI.

Notable public (unauthenticated) endpoints:
- `GET  /s/{slug}` — download-share landing page
- `POST /s/{slug}` — password submit; 302 to a short-lived Blob SAS URL
- `GET  /u/{slug}` — upload-request landing page
- `POST /u/{slug}` — receive file via per-request SAS write URL
- `GET  /sign/{participantId}?t={token}` — signature-signer flow
- `GET  /privacy` · `GET  /support` · `GET  /imprint` — required legal pages

---

## Repo layout

```
src/
  NimShare.Core/                # entities, DbContext, migrations (SQLite)
  NimShare.Migrations.SqlServer/# handwritten SqlServer migrations
  NimShare.Api/                 # ASP.NET Core app (MVC + Web API + Razor)
    Controllers/                # /api/v1/*, /settings/*, share/upload landings
    Services/                   # AiProvider, BlobStorage, FileAccess, Backup, …
    Views/                      # Razor MVC UI (Tungsten-branded)
    Resources/                  # 6 .resx files (en/de/fr/it/es/nl)
    wwwroot/                    # site.css, JS, static assets
ios/
  project.yml                   # xcodegen source of truth
  NimShare/Sources/*.swift      # SwiftUI iOS app
docs/                           # ARCHITECTURE, CONCEPT, API, COSTS, DEV_SETUP
infra/azuredeploy.json          # 1-click ARM template
VERSION                         # single source of truth for the app footer
```

---

## License

MIT — see [`LICENSE`](LICENSE).

**Trademark note:** *Tungsten Automation*, the Tungsten logo, and product names referenced in the design system are property of Tungsten Automation Corporation. This project uses their brand tokens for internal look-and-feel consistency only.
