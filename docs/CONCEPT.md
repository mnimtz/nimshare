# NimShare — Concept

> A Seafile-style file-sharing service, purpose-built for the Tungsten/Printix internal ecosystem: minimal to operate, generous in features, brand-consistent.

## Goals

1. **Send a file** — upload once, share a link, done. This is the 80% use case and must feel Dropbox-fast.
2. **Send with rules** — expiry, password, download cap, one-click revoke. This is what a plain Blob SAS can't deliver.
3. **Send at scale** — many users, multi-GB files, no server tunneling the bytes.
4. **Send on-brand** — every touchpoint (dashboard, share landing, emails) speaks Tungsten Automation visual language.
5. **Send in the user's language** — EFIGS at every layer.
6. **Send from anywhere** — the same server that powers the web must also power future iOS/Android clients.

## Non-goals

- **Two-way desktop file sync** (that's Seafile's / OneDrive's job — not worth reinventing).
- **Real-time collaboration on documents** (that's SharePoint / Google Docs).
- **General-purpose object storage API** — this is a *sharing* product on top of blobs, not a blob replacement.

## Feature catalogue

### Share (download) links

| Feature | MVP | Notes |
|---|---|---|
| Custom slug | ✅ | Alphanum + `-` + `_`; 3–64 chars; globally unique. Random fallback if left blank. |
| Password protection | ✅ | bcrypt-hashed; server checks per request → then SAS. |
| Expiry (absolute) | ✅ | User picks date/time; UTC in DB. |
| Max downloads | ✅ | Atomic counter; link auto-locks once reached. |
| Revoke without delete | ✅ | Boolean flag; file remains. |
| Multiple links per file | ✅ | Different rules per audience. |
| Landing page message | ✅ | Markdown allowed; sanitized. |
| Access statistics | ✅ | Per link: hits, downloads, last access; per event: timestamp, IP hash (salted), UA, referer. |
| QR code | ✅ | Rendered PNG/SVG next to the link. |
| Email notification on download | ✅ | Owner opt-in per link. |
| Whitelist by email | 🟡 | Post-MVP; requires recipient auth flow. |

### Reverse-share (upload / file-request) links

| Feature | MVP | Notes |
|---|---|---|
| Public upload landing | ✅ | Drag-drop; anonymous or optional name/email. |
| Expiry + max uploads | ✅ | Same guard rails. |
| Optional password | ✅ | Same as download links. |
| Owner notification | ✅ | Email when file lands. |
| Uploader-side confirmation | ✅ | Nice landing after success. |

### Folder sharing

| Feature | MVP | Notes |
|---|---|---|
| Share a folder as one link | ✅ | Prefix-based; all files under it, recursive. |
| Browse mode | ✅ | Recipient sees a mini file browser. |
| ZIP download | ✅ | Streamed server-side (no temp file); large-folder friendly. |

### Multi-user

| Feature | MVP | Notes |
|---|---|---|
| Sign-in with Entra ID | ✅ | Personal MSA + org via B2C. |
| Isolated file/link namespace | ✅ | All queries scoped by `OwnerId`. |
| Admin role | ✅ | Tenant-wide view: users, storage used, top links. |
| Quotas | ✅ | Per user; enforced at upload SAS issuance. |
| API tokens | 🟡 | Post-MVP: PATs for CI/scripts. |

### Custom domains per user

| Feature | MVP | Notes |
|---|---|---|
| Add domain in Settings | ✅ | `share.example.com` |
| TXT-record verification | ✅ | System issues a token; user adds `_nimshare-verify` TXT; app polls DNS. |
| Auto-bind to App Service | ✅ | Uses Azure REST API + managed identity. |
| Free Managed Certificate | ✅ | Requires App Service Standard tier. |
| Slug uniqueness | ✅ | Global — a slug resolves regardless of host header. |
| Wildcard domains | ⚪ | Explicitly out of scope for MVP. |

### Mobile-readiness (API-first)

- All state changes live under `/api/v1/*` — versioned, JSON, JWT-bearer.
- No hidden HTML-only endpoints; the web UI is a first-class client, not a privileged one.
- MSAL SDKs (JS, Swift, Kotlin) share the same Entra registration.
- Upload/download bypass the API server: browser/app POSTs directly to a Blob SAS URL the API returns.
- Push notifications (Azure Notification Hubs) planned but not wired — the hooks are there.

## Data model

```
User ─┬─< StorageFile ─< ShareLink ─< ShareLinkAccess
      │        │
      │        └─────< UploadRequestLink >──── (creates StorageFile on success)
      │
      ├─< CustomDomain
      └─< AuditEvent
```

### `User`

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| EntraOid | string | Object id from Entra token — the join key |
| DisplayName | string | From token |
| Email | string | From token |
| Role | enum | `User`, `Admin` |
| QuotaBytes | long | Default 10 GB |
| PreferredCulture | string(5) | `en`, `de`, `fr`, `it`, `es` |
| CreatedAt | datetimeoffset | |

### `StorageFile`

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| OwnerId | Guid | FK User |
| Name | string(255) | Original filename |
| SizeBytes | long | |
| ContentType | string(120) | |
| BlobPath | string(300) | `users/{owner}/{fileId}/{name}` |
| ContainerName | string(60) | |
| Sha256 | string(64) | Optional, populated on complete |
| CreatedAt | datetimeoffset | |
| DeletedAt | datetimeoffset? | Soft-delete |

### `ShareLink`

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| FileId | Guid | FK StorageFile |
| OwnerId | Guid | FK User (denormalised for query speed) |
| Slug | string(64) | Unique index |
| PasswordHash | string? | bcrypt |
| ExpiresAt | datetimeoffset? | Null = never |
| MaxDownloads | int? | Null = unlimited |
| DownloadCount | int | Atomic increment |
| HitCount | int | Includes landings that never triggered a download |
| Message | string(2000)? | Markdown |
| NotifyOnAccess | bool | Owner opt-in |
| IsRevoked | bool | |
| CreatedAt | datetimeoffset | |
| LastAccessAt | datetimeoffset? | |

### `ShareLinkAccess`

Append-only event log. Aggregated views come from this.

| Field | Type |
|---|---|
| Id | long (identity) |
| ShareLinkId | Guid |
| At | datetimeoffset |
| Kind | enum: `Landing`, `PasswordFail`, `Download` |
| IpHash | string(64) | HMAC-SHA256 with server salt — not the raw IP |
| UserAgent | string(300) |
| Referer | string(300)? |

### `CustomDomain`

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| OwnerId | Guid | FK User |
| Hostname | string(253) | Lowercase, unique |
| VerificationToken | string(64) | Guid-like |
| VerificationStatus | enum: `Pending`, `Verified`, `Failed` |
| CertificateStatus | enum: `None`, `Provisioning`, `Issued`, `Expired` |
| AddedAt | datetimeoffset | |
| VerifiedAt | datetimeoffset? | |

### `UploadRequestLink`

Same shape as `ShareLink` but on the other direction; on successful upload creates a `StorageFile` owned by the link's owner.

## Public URL layout

| Path | Purpose |
|---|---|
| `/` | Landing / login (if not authed) |
| `/dashboard` | Authenticated home |
| `/upload` | New file + link wizard |
| `/links` | List/manage the user's links |
| `/settings/*` | Profile, domains, quota |
| `/s/{slug}` | Public download landing (server-branded) |
| `/u/{slug}` | Public upload landing |
| `/api/v1/*` | JSON API |
| `/swagger` | OpenAPI (Dev only) |

## Security model

- **Never emit long-lived SAS.** SAS URLs served by `/s/{slug}` after rule-check are TTL ≤ 60 seconds and single-file-scoped.
- **Password check happens server-side**, before any SAS is issued. A dumped share password gives an attacker no direct blob access.
- **Rate limiting** on `/s/{slug}` POST (password submit) — sliding window per link+IP.
- **IP hashing**, not storage — a rotating server-side salt HMAC-SHA256s IPs before persistence.
- **Slug enumeration resistance**: unknown slugs return the same "link not found" page as expired ones, in constant time.
- **Content sniffing**: filenames are stored verbatim but served with `Content-Disposition: attachment; filename*=UTF-8''…` and a locked `Content-Type` from the original upload's declared MIME; the SAS response header override handles this.
- **Anti-abuse**: Defender for Storage can be flipped on at the storage account level in production; the app tags files with the uploader for takedown workflows.
- **CSRF**: cookie-based sessions on the web are `SameSite=Lax`, all state-changing forms use antiforgery tokens; the API accepts bearer tokens only, no cookies, so no CSRF vector there.

## Cost model (indicative, USD/month, EU-West)

| Service | SKU | Est. |
|---|---|---|
| App Service Plan | S1 (needed for custom domains + free Managed Cert) | ~ 73 |
| Azure SQL DB | Basic | ~ 5 |
| Storage Account | Hot LRS, 100 GB, 100 GB egress | ~ 6 |
| Application Insights | Pay-as-you-go, 5 GB | ~ 12 |
| Key Vault | Standard | ~ 1 |
| **Total** | | **~ 97 USD** |

Scaling levers: move App Service to B1 (drop cost to ~55 USD but lose managed certs — need Front Door for TLS then), switch SQL to serverless auto-pause for very-low-usage tenants, tier storage to Cool for older files via lifecycle rules.

## Explicit trade-offs

- **Chose Razor Pages over a SPA** for the web UI — one less build system, matches tonerwatch. Mobile apps hit the same JSON API the Razor pages consume via `HttpClient`.
- **Chose Azure SQL over Table Storage** — link revoke/count updates need transactions; join-heavy stats queries want a relational engine. The extra ~5 USD/mo is worth it.
- **Chose SAS-redirect over server-tunnel** — an extra hop but keeps the App Service plan tiny even under 100 GB downloads.
- **Chose Entra ID over local accounts** — no password reset UX to build, MFA for free, but you inherit the tenant onboarding overhead. B2C bridges to consumer identities.
