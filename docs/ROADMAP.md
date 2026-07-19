# NimShare Roadmap

Stand: v1.4.3 · Feature-Parität mit **Seafile Community** ist das Ziel, differenziert wird auf der AI-Achse.

## Was in v1.4.x gelandet ist (nach dem v1.3-Zyklus)

- **v1.4.0** — Mobile-Topbar kompakt, alle Server-Meldungen lokalisiert (30+ Keys × 6 Sprachen), moderne User-Edit-Page
- **v1.4.1** — Sprachauswahl bleibt persistent (Cookie + DB)
- **v1.4.2** — Landing-Templates (Global Admin + Personal User) mit Logo/Banner/Farbe/Markdown, Tungsten-Hinweis entfernt
- **v1.4.3** — **Vision-AI** (Bilder werden beschrieben statt „nicht unterstützt"), **Landing-Preview** (Bild inline, PDF iframe), **Link-Detail-Report** unter `/links/{id}` mit 30-Tage-Chart, Referrer-Breakdown, Log

## Was Seafile hat und wir noch NICHT

Nach der Recherche früher im Zyklus, sortiert nach Aufwand-vs-Wert.

### ★★★ Muss noch — nächste 2 Releases

1. **File-Versions / History** — auto-versionierung mit „letzte Version wiederherstellen" aus dem Rechtsklick. Bei Seafile das Kern-Feature.
2. **Sub-Folder-Permissions** — Read-only auf `Group/Verträge/2026` obwohl das gesamte Group-Repo Write ist. In Seafile Pro gratis, bei uns aktuell nur Grant-per-File/Folder ohne Vererbungs-Override.
3. **Fulltext-Search** — der Embedding-Extract enthält bereits die extrahierten Texte. Ich muss sie als klassischen Volltext-Index zugreifbar machen (nicht nur semantisch). Massives UX-Delta.
4. **2FA (TOTP)** für lokale Accounts. Entra deckt sich selbst ab.
5. **Public-Link Rezipienten-Allowlist** — nicht nur Passwort, sondern „E-Mail muss auf X@Y sein" oder „muss vorher einen OTP klicken".
6. **Notifications** — In-App-Feed + optional E-Mail-Digest wenn Direct-Share dich erreicht oder ein Download stattfindet.

### ★★ Nice to have — v1.5

7. **OnlyOffice / Collabora** — Word/Excel im Browser bearbeiten, Co-Auth. Free bei Seafile CE.
8. **File-Locking** — automatisch beim Office-Edit + manuell.
9. **Wiki in einer Library** — Markdown-Editor, Seiten-Baum. Kommt v13 von Seafile.
10. **Custom Metadata + Views** — Notion-artige Spalten/Filter über Files. Kommt v13 von Seafile.
11. **WebDAV-Endpoint** — dünner Adapter, spart eigenen Sync-Client. Rclone + Windows Netzlaufwerk out-of-the-box.
12. **Webhooks + scoped API-Tokens** — Power Automate / Logic Apps.
13. **Wiederkehrende Uploads** — Upload-Link „soll wöchentlich Berichte annehmen".
14. **Ordner-Zip-Download** ist da, aber ohne Fortschritt-Feedback bei großen Ordnern.

### ★ Langfristig

15. **Desktop-Sync-Client** — Windows/mac. Multi-Quartals-Projekt.
16. **Native Mobile-Apps** — iOS haben wir (SwiftUI). Android fehlt komplett.
17. **Virtual Drive / Cloud Mount** — nach Sync-Client sinnvoll.
18. **Virus-Scanning-Hook** — ClamAV/Defender an Complete-Endpoint.
19. **E2E-verschlüsselte Libraries** — Server sieht nix. Zerlegt AI-Features auf der Library.
20. **SAML/OIDC generisch** — nicht nur Entra. LDAP-Sync für lokal.
21. **Realtime-Notifs** via WebSocket — kein Refresh nötig.

## Was wir haben, was Seafile nicht hat (Differenzierung)

- **AI-Summary** aller Dateien inkl. **Vision für Bilder** (v1.4.3)
- **AI-Chat mit deinen Dateien** (RAG über Embeddings)
- **AI-Draft** für Share-Emails
- **AI Risk-Detection** auf Public-Uploads (PII/Credit-Card/Secret)
- **Semantische Suche** (nicht nur Volltext)
- **Landing-Templates pro User + global** (per-User Whitelabel des Download-Bereichs)
- **Direct-Share mit Group-Vererbung** (unser Modell walkt automatisch die Ordner-Vorfahren-Kette)
- **Native iOS-App** mit voller v1.3-Parität

## Explizite Nicht-Ziele

- Gesichtserkennung in Photos (Seafile-Feature, nicht Enterprise-relevant)
- Twilio-2FA (nur TOTP)
- Alles was Entra schon macht (SSO, MFA, RBAC via Entra-Groups)
