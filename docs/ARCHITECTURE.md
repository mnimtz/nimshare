# Architecture

## Request lifecycles

### Uploading a file (authenticated)

```
Browser ── POST /api/v1/files (name, size, contentType) ──► API
                                                            │
                                                            ├─ auth: JWT valid?
                                                            ├─ quota: owner has room?
                                                            ├─ create StorageFile row (Pending)
                                                            └─ issue write-SAS (30 min TTL)
Browser ◄──── { fileId, uploadUrl (SAS), headers } ─────────┘

Browser ── PUT {uploadUrl} (bytes, chunked) ─────────► Blob Storage
Browser ── POST /api/v1/files/{id}/complete ─────────► API
                                                       │
                                                       ├─ HEAD the blob → verify size
                                                       ├─ mark row Ready
                                                       └─ 204
```

### Serving a share link

```
Recipient ─ GET /s/{slug} ─────────────────────► API (public)
                                                   │
                                                   ├─ lookup by slug (constant time on miss)
                                                   ├─ expired?  revoked?  cap hit?
                                                   ├─ if passworded → render password page
                                                   └─ render landing (message, file meta, "Download")
                                                          │
Recipient ─ POST /s/{slug} (password if any) ──► API
                                                   │
                                                   ├─ verify pw (bcrypt)
                                                   ├─ atomic: DownloadCount++, LastAccessAt=now
                                                   ├─ log ShareLinkAccess row
                                                   ├─ issue read-SAS (60 s TTL, response-content-disposition set)
                                                   └─ 302 → SAS URL
Recipient ── GET {SAS URL} ──────────────────► Blob Storage (direct)
```

## Component map

| Component | Tech | Responsibility |
|---|---|---|
| Web + API | ASP.NET Core 8 (Razor Pages + Controllers) | UI, JSON API, public link handler |
| Auth | Microsoft.Identity.Web | Entra ID / B2C token validation, sign-in flow |
| DB access | EF Core 8 | Migrations, queries |
| Storage | Azure.Storage.Blobs | SAS issuance, blob metadata |
| Search / analytics | (deferred) | Postgres pg_trgm or SQL FTS — out of scope for MVP |
| Cert issuance | Azure Resource Manager REST | Bind custom hostname + issue Managed Cert |
| DNS verification | System.Net.DnsClient (`Dns.GetTxtRecords`) | Poll TXT record for verification |
| QR codes | `QRCoder` NuGet | SVG/PNG generation |
| ZIP stream | `System.IO.Compression.ZipArchive` | Folder-download response body |
| Notifications | `MailKit` (SMTP) or Azure Communication Services | Owner alerts, transactional email |
| Localization | ASP.NET Core `IStringLocalizer` + `.resx` | EFIGS surface |

## Multi-tenancy model

Single Azure Storage account, single container (`files`), path-prefixed by user id:

```
files/
  users/
    <userGuid>/
      <fileGuid>/
        <originalName>
```

Rationale: 500 containers is Azure's soft cap; one-per-user doesn't scale to thousands of users. Path-prefix isolation with SAS-scoped-to-blob gives equivalent security guarantees. The SAS issued for a download is *file-scoped*, not container-scoped — even if it leaked, it only exposes that one blob.

## Custom-domain binding

1. User inputs `share.example.com` in Settings.
2. App generates `VerificationToken` (random 32-byte, base32-encoded).
3. App shows the user: *"Add TXT record `_nimshare-verify.share.example.com` with value `<token>`, then CNAME `share.example.com` → `<siteName>.azurewebsites.net`."*
4. App polls DNS every 30 s for up to 30 min looking for the TXT.
5. Once found, calls the Azure REST API to add the hostname binding to the App Service and request a Managed Certificate.
6. On next request, the middleware maps `Host: share.example.com` → owner via `CustomDomain` table; `Slug` lookup remains global.

Managed identity of the App Service needs `Website Contributor` on itself. Cheapest way to grant this via the ARM template is a role assignment at deploy time.

## Localization

- ASP.NET Core `Startup`: `builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");` + `AddViewLocalization()` + `AddDataAnnotationsLocalization()`.
- Supported cultures: `en`, `de`, `fr`, `it`, `es`.
- Request culture provider order:
  1. Explicit query `?ui-culture=de` (bookmarkable share links can carry this).
  2. Cookie `.AspNetCore.Culture` (user preference from selector).
  3. User's `PreferredCulture` (from DB) if authenticated.
  4. `Accept-Language` header.
  5. Fallback `en`.
- `SharedResources.resx` is the neutral file; per-culture files sit next to it. Marker class `SharedResources` lives in `NimShare.Core.Resources` so `IStringLocalizer<SharedResources>` works in both API controllers and Razor pages.

## Observability

- **App Insights** SDK auto-instruments requests, dependencies, exceptions.
- Custom events: `link.created`, `link.accessed`, `link.download.blocked_password`, `link.download.blocked_expired`, `upload.completed`, `quota.exceeded`.
- **Log Analytics workspace** attached, retention 30 d for MVP.
- Basic Grafana-friendly Kusto queries in [`docs/OPERATIONS.md`](OPERATIONS.md) *(TODO)*.
