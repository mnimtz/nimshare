# NimShare Roadmap

Feature parity with **Seafile Community**, then differentiation on the AI axis.

## v1.3 — landed

- **Direct share** to specific user or group with **Read / Write permission**
  (in the file-manager right-click menu, or the ⭐ menu — "Freigeben an…")
- **Trash / Restore / Purge** (soft-delete, blob kept until purge)
- **Favorites** — star any file or folder, quick access via sidebar
- **Shared-with-me** page — everything others have granted to you
- **Activity feed** — per-user history + admin-wide view
- **Group membership editor** in Admin → Users (per-user multi-select of groups)
- **Enriched right-click menu** — Preview, Share link, Share to user, Favorite,
  Upload-request, Rename, Move, Delete (context-aware per kind + rights)
- **Mobile v2** — proper off-canvas drawer, iOS-safe font sizing (no zoom on
  input focus), single-column tables, horizontal-scrolling breadcrumbs,
  touch-always row actions, 100 vw modal panels

## v1.4 — next

- **File versions** — auto-history per library (keep N versions or N days),
  "restore previous version" from right-click
- **Sub-folder permissions inside a group library** (limit member access to a
  branch only)
- **Full-text search** — surface the extracted-text index (already stored for
  embeddings) as classic keyword search alongside the semantic one
- **Public link email verification / recipient allow-list**
- **Notifications** — in-app + optional email digest on direct-share events

## v1.5 — differentiation

- **2FA (TOTP)** for local accounts
- **WebDAV endpoint** — unlocks Windows "Map network drive", rclone, mobile Office
- **OnlyOffice or Collabora integration** for in-browser Office edit + co-auth
- **File locking** (auto on Office edit + manual)
- **Custom metadata + views** (Notion-like columns/filters over your files)
- **Wiki in a library** (markdown pages, in-app editor)
- **Webhooks + scoped API tokens**

## Long-term

- Desktop sync client (Windows / mac)
- Virtual drive / cloud mount
- Native iOS / Android apps (currently: SwiftUI iOS app in `ios/`)
- Server-side virus scanning hook
- End-to-end encrypted libraries (blocks AI features on those libs)
- SAML / OIDC beyond Entra ID, LDAP group sync
- Real-time notification server (WebSocket-driven UI updates)

## Explicit non-goals

- Face detection in photos
- Twilio 2FA (TOTP-only)
- Rebuilding features already covered by Entra (SSO, MFA on Entra accounts, RBAC via Entra groups)
