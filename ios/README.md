# NimShare iOS

Native SwiftUI companion app for a self-hosted NimShare server.

## Features

- Sign in with email + password (JWT). Server URL is configurable — point it at your own instance.
- **Files**: browse Personal / Public / Group libraries with folder navigation, sizes, owners, AI tags and risk badges.
- **Preview**: native QuickLook for PDFs, images, Office docs, videos, etc.
- **Search**: semantic search across all files you can access (needs AI provider configured on the server).
- **Chat**: RAG chat with citations, tap a citation to preview the source file.
- **Links**: list of your share links with copy / native share sheet.
- **Profile**: avatar, quota, sign out, change server.

## Requirements

- Xcode 15+, iOS 17+
- [xcodegen](https://github.com/yonaskolb/XcodeGen) — `brew install xcodegen`
- A running NimShare server (v0.9.1+ — the JSON auth endpoints were added in that release).

## Setup

```bash
cd ios
xcodegen generate
open NimShare.xcodeproj
```

In Xcode:
1. Select the **NimShare** target, then the **Signing & Capabilities** tab.
2. Set your **Team** (Apple ID). Bundle ID stays `email.nimtz.nimshare`.
3. Plug in your iPhone (or pick the simulator) and hit ⌘R.

## First run

1. Enter your NimShare server URL (`https://your.nimshare.tld`).
2. Sign in with an existing NimShare local account.
3. All views populate from the server — no separate mobile setup needed.

## Architecture

- Pure SwiftUI, no third-party dependencies.
- `NimShareAPI` — small async URLSession client with ISO-8601 date handling.
- `AuthStore` — `@ObservableObject` state machine (booting → needsServer / needsLogin → signedIn). Token in Keychain, server URL in UserDefaults.
- Views map 1:1 to server endpoints — adding a new API is a matter of adding a struct in `Models.swift` and a method on `NimShareAPI`.

## Backend endpoints used

- `POST /api/v1/auth/login`
- `GET  /api/v1/auth/me`
- `GET  /api/v1/browse/scopes`
- `GET  /api/v1/browse/list?scope=&groupId=&path=`
- `GET  /api/v1/files/{id}/preview-url`
- `GET  /api/v1/links`
- `POST /api/v1/ai/search`
- `POST /api/v1/ai/chat`

Uploads and admin views intentionally live on the web app for now.
