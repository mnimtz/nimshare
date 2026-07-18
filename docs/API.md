# API v1

All endpoints under `/api/v1/*` require `Authorization: Bearer <JWT>` from Entra ID.
Public share endpoints (`/s/*`, `/u/*`) are unauthenticated but rule-checked.

Responses are JSON, using RFC 7807 problem details on 4xx/5xx.

---

## Files

### `POST /api/v1/files`

Start an upload. Returns a write-SAS URL the client uses to upload the bytes directly to Blob Storage.

```json
Request
{
  "name": "quarterly-report.pdf",
  "sizeBytes": 4823904,
  "contentType": "application/pdf"
}
```

```json
201 Created
{
  "fileId": "3f1d…",
  "uploadUrl": "https://<account>.blob.core.windows.net/files/users/…?sv=…&sig=…",
  "uploadMethod": "PUT",
  "expiresAt": "2026-07-18T09:00:00Z"
}
```

### `POST /api/v1/files/{id}/complete`

Signal that the upload is done. The server HEADs the blob to verify.

```json
204 No Content
```

### `GET /api/v1/files`

List the caller's files. Supports `?page=1&pageSize=50&folder=…&search=…`.

### `PATCH /api/v1/files/{id}` — rename, move to folder.

### `DELETE /api/v1/files/{id}` — soft-delete + queue blob for delete.

---

## Share links

### `POST /api/v1/links`

```json
{
  "fileId": "3f1d…",
  "slug": "project-x",              // optional; if omitted, a random slug is generated
  "password": "s3cret",             // optional
  "expiresAt": "2026-08-01T00:00Z", // optional
  "maxDownloads": 25,               // optional
  "message": "Here's the deck we discussed.",
  "notifyOnAccess": true
}
```

```json
201 Created
{
  "id": "…",
  "slug": "project-x",
  "url": "https://<host>/s/project-x",
  "qrCodeUrl": "/api/v1/links/…/qr.svg",
  "expiresAt": "2026-08-01T00:00Z",
  "maxDownloads": 25,
  "downloadCount": 0,
  "createdAt": "2026-07-18T08:45:00Z"
}
```

Errors:
- `409 slug_taken` — that slug is already in use.
- `422 invalid_slug` — doesn't match `^[a-z0-9](?:[a-z0-9_-]{1,62}[a-z0-9])?$`.
- `403 file_not_owned` — the fileId doesn't belong to the caller.

### `GET  /api/v1/links` — list caller's links.
### `GET  /api/v1/links/{id}` — full detail incl. counters.
### `GET  /api/v1/links/{id}/stats` — recent access events.
### `GET  /api/v1/links/{id}/qr.svg` — QR code as SVG.
### `PATCH /api/v1/links/{id}` — change expiry / max / message / revoked.
### `DELETE /api/v1/links/{id}` — hard-delete link (file untouched).

---

## Upload-request (reverse) links

### `POST /api/v1/upload-requests`

```json
{
  "slug": "send-me-your-invoice",
  "password": null,
  "expiresAt": "2026-08-01T00:00Z",
  "maxUploads": 5,
  "message": "Drop the PDF here — thanks!"
}
```

Returns a public URL `https://<host>/u/{slug}`. Recipient POSTs the file; the server issues a per-upload write-SAS bound to a fresh `StorageFile` row owned by the request creator.

---

## Custom domains

### `POST /api/v1/custom-domains` — add a hostname; returns `VerificationToken`.
### `POST /api/v1/custom-domains/{id}/verify` — kick off DNS TXT check.
### `GET  /api/v1/custom-domains` — list caller's domains + status.
### `DELETE /api/v1/custom-domains/{id}` — unbind (also removes from App Service).

---

## Public share endpoints (unauthenticated)

### `GET /s/{slug}`

Renders the branded landing page for the download. HTML response. `Accept-Language` decides the language.

If the link needs a password, the page shows a password form (POSTs back to the same URL).

### `POST /s/{slug}`

Form-encoded `password=…`. On success, `302 Location: <short-lived SAS URL>` and increments the counter atomically.

### `GET /u/{slug}` / `POST /u/{slug}`

Same pattern, for upload-request links.

---

## Errors

Standard problem-details:

```json
{
  "type": "https://nimshare.example.com/problems/link-expired",
  "title": "Link expired",
  "status": 410,
  "detail": "This link expired on 2026-07-01T00:00:00Z.",
  "instance": "/s/project-x"
}
```
