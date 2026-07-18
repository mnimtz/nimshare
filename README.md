# NimShare

**Self-hosted, brand-styled file sharing on Azure.** Upload once, share via personalised links with expiry, password, download limits, custom domains, QR codes, and file-request (upload) links. Multi-user, API-first, EFIGS-localised, ready for iOS/Android clients.

Built on **Azure App Service + Azure Blob Storage**. Deploy in one click.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fmnimtz%2Fnimshare%2Fmain%2Finfra%2Fazuredeploy.json)
[![Visualize](https://raw.githubusercontent.com/Azure/azure-quickstart-templates/master/1-CONTRIBUTION-GUIDE/images/visualizebutton.svg)](http://armviz.io/#/?load=https%3A%2F%2Fraw.githubusercontent.com%2Fmnimtz%2Fnimshare%2Fmain%2Finfra%2Fazuredeploy.json)

![Tungsten Automation](https://img.shields.io/badge/brand-Tungsten%20Automation-002854?style=flat)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat)
![License: MIT](https://img.shields.io/badge/license-MIT-brightgreen?style=flat)
![Localisation: EFIGS](https://img.shields.io/badge/i18n-EFIGS-00A0FB?style=flat)

---

## Highlights

### Personalised share links
- **Custom slugs** — `nimshare.example.com/s/project-x` instead of random tokens.
- **Password protection** — bcrypt-hashed passphrases, verified server-side per request.
- **Expiration** — absolute date/time or "burn after N downloads".
- **Download limits** — hard cap per link, atomically counted.
- **Multiple links per file** — different rules for different audiences (e.g. one link with password for client A, one internal without).
- **Optional message + branded landing page** shown before download.
- **QR code** auto-generated for every link.
- **One-click revoke** without deleting the file.
- **Access statistics** — per link: hits, downloads, last access, IP hash, referer.

### Reverse-share (upload/file-request) links
- Generate a public link that lets **someone else send a file to you** — no account needed on their side.
- Same guard rails: expiry, max uploads, optional password.

### Folder sharing
- Share a whole folder as a single link — recipient sees a lightweight file browser, or downloads a streamed ZIP.

### Multi-user & tenant isolation
- Each user gets an isolated storage prefix (`users/{userId}/…`) and can only manage their own files/links.
- **Two roles**: `User` (own files/links only) and `Administrator` (full user management, disable/enable, delete).
- **Two sign-in methods, both optional to mix**:
  - **Local email + password** — always works. First user to hit the app is auto-promoted to Admin (first-run setup wizard).
  - **Microsoft Entra ID** — optional, layered on top when configured (see [`docs/DEV_SETUP.md`](docs/DEV_SETUP.md)). Existing local accounts get auto-linked by email on first Entra sign-in.
- Admins manage users under **Settings → Users**: create, disable, change role, delete. Safety rails prevent demoting or deleting the last active admin.

### Custom domains per user (Settings → Domains)
- Add `share.your-company.com` to your account.
- Verify via TXT record.
- Azure App Service Managed Certificate is issued automatically once DNS is aligned.
- Global slugs remain unique; the same slug resolves regardless of which of your bound domains was used.

### Mobile-ready
- Backend is a strict JSON REST API (`/api/v1/*`) — the web UI is just its first client.
- Token-based OAuth2/OIDC auth via **MSAL** — iOS/Android SDKs plug in cleanly.
- Uploads/downloads go **directly** browser/app ↔ Blob Storage over short-lived SAS URLs, so mobile data plans aren't burned tunneling through the server.

### Localisation — EFIGS by default
Every user-facing string is provided in **English, French, Italian, German, Spanish**. Public share landing pages honour the visitor's `Accept-Language` header; the app's own UI has a language switcher.

### Tungsten-branded UI
Follows the [Tungsten Automation Brand Book](https://tungstenautomation.com/) — primary navy `#002854`, accent blue `#00A0FB`, yellow CTAs `#FFC600`, Red Hat Display, blue-domain gradient sidebar. Consistent look-and-feel with sibling internal projects like [printix-tonerwatch](https://github.com/mnimtz/printix-tonerwatch).

---

## Architecture at a glance

```
                       ┌──────────────────────────┐
    Web / iOS / Android │        NimShare API      │
      (public link too) │  ASP.NET Core 8 · MVC +  │
   ────────────────────►│  Web API · Razor Pages   │
                       └────────────┬─────────────┘
                                    │  1) Auth (Entra)
                                    │  2) Rules (pw / expiry / count / revoked)
                                    │  3) Issue SAS (≤ 60 s TTL)
                                    ▼
                      ┌──────────────────────────────┐
   Direct ────────►   │  Azure Blob Storage           │
   upload/download    │  files/users/{uid}/{fid}/...  │
                      └──────────────────────────────┘

           ┌────────────────┐        ┌──────────────────┐
           │  Azure SQL DB  │        │  App Insights +  │
           │  (metadata)    │        │  Log Analytics   │
           └────────────────┘        └──────────────────┘
```

Deep-dive in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and the feature/data-model spec in [`docs/CONCEPT.md`](docs/CONCEPT.md).

---

## Deploy

### 1-click on Azure

Click the **Deploy to Azure** button at the top. Only one parameter is required:

| Parameter | Default | Meaning |
|---|---|---|
| `siteName` | *(required)* | Globally unique App Service name → `https://<siteName>.azurewebsites.net` |
| `sku` | `B1` | Plan SKU. `B1` ≈ 10 EUR/month, always-on. `F1` free but sleeps. `S1`+ needed if you want custom domains with free managed TLS certs. |
| `location` | resource-group region | Azure region |
| `containerImage` | `ghcr.io/mnimtz/nimshare:latest` | Or pin a specific tag like `ghcr.io/mnimtz/nimshare:0.1.0` for reproducibility |
| `tz` | `Europe/Berlin` | IANA timezone for link timestamps |
| `defaultLang` | `en` | Fallback UI language (en/fr/it/de/es) |

What the template provisions:

- **Linux App Service** running the container from GHCR (`ghcr.io/mnimtz/nimshare`)
- **Storage Account** with:
  - a `files` Blob container for user uploads
  - a `nimshare-data` File share mounted at `/data` for the SQLite metadata DB
- **Managed identity** on the App Service, granted `Storage Blob Data Contributor` on the storage account — no connection strings, no account keys in app settings

**No SQL Database, no Key Vault, no Entra ID parameters up front.** The app boots and the public welcome page renders immediately. To turn on sign-in, follow the post-deploy step below.

### First run

Open your site → NimShare shows a **First-run setup wizard**. Enter your email, name and a password → that account is created as the first Administrator. You're immediately signed in and land on the dashboard.

From that moment: local email+password sign-in works via `/login`. Additional users can be created under **Settings → Users** (Admin-only page).

### Optional: enable Microsoft Entra sign-in on top

If you also want "Sign in with Microsoft" alongside the local login:

1. Create an Entra ID app registration (see [`docs/DEV_SETUP.md`](docs/DEV_SETUP.md); redirect URI is `https://<siteName>.azurewebsites.net/signin-oidc`).
2. In the Azure portal, App Service → **Configuration** → **Application settings**:
   - `AzureAd__TenantId` = your tenant GUID (or `common` for multi-tenant)
   - `AzureAd__ClientId` = the app registration's Application (client) ID
3. Save, then **Restart** the App Service.

The login page now shows both: the email/password form + a "Sign in with Microsoft" button.

### Cost estimate

- App Service `B1`: ~10 EUR/month
- Storage account (100 GB Blob + 5 GB File share): ~2 EUR/month
- **Total: ~12 EUR/month** at personal-use volumes

See [`docs/COSTS.md`](docs/COSTS.md).

### Note on the GHCR image

The first time you deploy, make sure the container package is public: <https://github.com/users/mnimtz/packages/container/nimshare/settings> → *Change visibility* → *Public*. Without this, App Service can't pull the image and you'll see a generic "Application Error" page.

### Local development

```bash
git clone https://github.com/mnimtz/nimshare.git
cd nimshare
dotnet restore
dotnet ef database update --project src/NimShare.Api
dotnet run --project src/NimShare.Api
```

Open <http://localhost:5099>. Sign-in requires a working Entra ID app registration — see `docs/DEV_SETUP.md`. For pure UI/CSS work, the public share landing (`/s/{slug}`) and the welcome page render without auth.

Uploads use an Azurite local Blob emulator by default. Start it with:

```bash
docker run -d --name azurite -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite
```

---

## Configuration

Everything is 12-factor-ish — see `src/NimShare.Api/appsettings.json` and the environment overrides the ARM template writes. The most important knobs:

| Key | Default | Purpose |
|---|---|---|
| `Storage:ContainerName` | `files` | Container used for user files |
| `Storage:DownloadSasTtlSeconds` | `60` | Lifetime of the redirect SAS |
| `Links:DefaultExpiryDays` | `30` | Default expiry when the user doesn't pick one |
| `Links:AllowCustomSlug` | `true` | Turn off if you want random-only slugs |
| `Localization:DefaultCulture` | `en` | Falls back to this if `Accept-Language` doesn't match EFIGS |
| `AzureAd:TenantId` | *(required)* | Entra ID tenant id or `common` for multi-tenant |
| `AzureAd:ClientId` | *(required)* | Entra ID app registration client id |

---

## API

Full OpenAPI spec is served at `/swagger` in Development, and the JSON at `/openapi/v1.json` in any environment.

Quick reference in [`docs/API.md`](docs/API.md). Auth is bearer JWT for `/api/v1/*`. Public endpoints:

- `GET  /s/{slug}` — landing page for a download share (renders the branded landing).
- `POST /s/{slug}` — password submit; on success 302-redirects to a short-lived Blob SAS URL.
- `GET  /u/{slug}` — landing page for an upload request.
- `POST /u/{slug}` — accepts the file to a per-request SAS write URL, notifies the owner.

---

## Roadmap

- ✅ MVP: multi-user, custom-slug download links with pw/expiry/max-downloads
- ✅ Tungsten-brand UI, EFIGS
- ✅ Deploy-to-Azure template
- 🟡 Custom domains per user (settings UI + verification flow)
- 🟡 Folder ZIP streaming
- 🟡 Upload-request (reverse) links
- 🟡 QR codes on link details
- 🟡 Email notifications on access
- ⚪ iOS app (SwiftUI, MSAL)
- ⚪ Android app (Jetpack Compose, MSAL)
- ⚪ WebDAV endpoint for desktop mounting
- ⚪ Optional end-to-end encryption for zero-knowledge links

---

## License

MIT — see [`LICENSE`](LICENSE).

Trademark note: *Tungsten Automation*, the Tungsten logo, and product names referenced in the design system are property of Tungsten Automation Corporation. This project uses their brand tokens for internal look-and-feel consistency only.
