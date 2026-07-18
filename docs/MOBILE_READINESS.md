# Mobile app readiness — status as of v0.5.0

**Verdict: yes, every capability in NimShare is reachable from an iOS or
Android app with a JWT bearer, with these caveats.**

## The rule we follow

Every state-changing action in the web UI has a corresponding JSON endpoint
under `/api/v1/…` that returns / accepts application/json and accepts a
`Authorization: Bearer <jwt>` token from Microsoft Entra ID.

The web UI's Razor forms are a **first-party client** of those same endpoints;
they don't have a private API surface. New features in v0.5.0 (profile
edit, invite user, email gateway, groups, browse) all follow this rule
except where noted below.

## Feature-by-feature status

| Feature | Web endpoint | Bearer-usable? | Notes |
|---|---|---|---|
| File upload | `POST /api/v1/files` + Blob SAS PUT | ✅ | Direct browser/app→Blob upload. Perfect for mobile. |
| File list | `GET /api/v1/files` | ✅ | |
| File delete | `DELETE /api/v1/files/{id}` | ✅ | |
| Share link create | `POST /api/v1/links` | ✅ | |
| Share link update | `PATCH /api/v1/links/{id}` | ✅ | |
| Share link revoke/delete | `DELETE /api/v1/links/{id}` | ✅ | |
| Send by email | `POST /api/v1/links/{id}/send-email` | ✅ | Added v0.4.0 |
| Upload-request create | `POST /api/v1/upload-requests` | ✅ | |
| Custom domains | `/api/v1/custom-domains/*` | ✅ | |
| **Groups CRUD** | via **HTML `/groups/*`** | ⚠️ **HTML-only** | See below |
| **Group members** | via HTML | ⚠️ **HTML-only** | |
| **User invite** | via HTML `/settings/users/invite` | ⚠️ **HTML-only** | |
| **Profile edit** | via HTML `/settings/profile` | ⚠️ **HTML-only** | |
| **Email gateway** | via HTML `/settings/email` | ⚠️ Admin only, no API planned |
| **Quota edit** | via HTML `/settings/users/{id}/set-quota` | ⚠️ Admin only |
| **Setup first admin** | HTML `/setup` | ⚠️ Web-only by design (one-shot) |

## The four HTML-only endpoints that need a JSON twin

Before we ship the iOS/Android client (v0.6+), we'll add:

1. `POST /api/v1/groups` / `GET` / `DELETE` — currently `GroupsController`
   is MVC-only.
2. `POST /api/v1/groups/{id}/members` / `DELETE /api/v1/groups/{id}/members/{userId}` — same.
3. `POST /api/v1/invites` — the invite flow (accept-invite already uses
   a token URL, that stays the same regardless of client).
4. `PATCH /api/v1/users/me` for profile edit + password change; users can already do this via web.

These are ~150 lines of extra controller code once we decide to build the app.
The domain logic (validation, safety rails, DB writes) already lives in
services — the API controllers become thin wrappers.

## Auth for the mobile app

- Use **MSAL for iOS** (Swift) or **MSAL for Android** (Kotlin) — same
  Entra app registration as the web.
- Bearer token acquired via MSAL's interactive flow → `Authorization`
  header on every `/api/v1/*` call.
- Existing `AddMicrosoftIdentityWebApi(..., jwtBearerScheme: "Bearer")` in
  `Program.cs` already validates those tokens.
- For **local-only accounts** (email + password): we'd need a `/api/v1/auth/login`
  JSON endpoint that returns a JWT signed by the app. This is straightforward
  (~40 lines) and not shipped yet — filed for v0.6.

## Direct-to-blob upload from mobile

The mobile app gets the SAS URL from `POST /api/v1/files`, then does a
`PUT {sasUrl}` with the file bytes directly — same as the web flow. This
is the reason NimShare works well on mobile data: the App Service never
sees the bytes.

Streaming on iOS: use `URLSession.uploadTask(with: request, fromFile: url)`.
On Android: `HttpURLConnection.setDoOutput(true)` with chunked streaming.
Both handle files up to the pause/resume boundary (~4 GB by default).

## Push notifications

Not wired yet. When we build the app, we'll add Azure Notification Hubs +
a `POST /api/v1/devices` endpoint to register the device token. Then the
existing `INotificationService` gets a "push" implementation next to the
"email" one.

## Language

The API respects `Accept-Language` on every response that returns localized
strings (currently only error `title`/`detail`). The mobile app should pass
the device locale as `Accept-Language: nl-NL,nl;q=0.9,en;q=0.5` and rely on
NimShare's supported cultures list.
