# NimShare Roadmap

Stand: v1.9.0 · Signaturen (inkl. Reassign + Zertifikate) + Azure-SQL-Wizard + Ordner-Baum/-Icons + Live-AI-Modelle.

## Erledigt seit v1.6.0

**v1.6.x — Signaturen**
- Visuelles Feld-Placement (pdf.js Drag-Drop)
- iOS-Signer via PencilKit
- Sequential Delivery + Deadlines + Auto-Reminder

**v1.7.0 – v1.7.3 — File-Locking, Wiki, Webhooks, Recurring Uploads**
- OnlyOffice/Collabora Integration (MVP)
- Wiki innerhalb einer Library
- Webhooks + Scoped API-Tokens
- Wiederkehrende Upload-Requests + Zip-Progress

**v1.7.13 – v1.7.23 — Backlog + UX**
- Signatur-Wizard, Bulk-Zip, i18n-Cleanup
- Signatur-Submit-Fix (Chrome-Bug btn.disabled=true blockierte Form-Submit)
- Ordner-Baum in linker Spalte + Rechtsklick-Icon-Änderung (Emoji + Farbe)
- Drag&Drop-Upload: Chunked-Upload (4 MB Blöcke, per-Chunk Retry), Live-Progress mit Speed + ETA
- Slug-Normalisierung (`PowerPDF` → `powerpdf`, `Rechnung 2025.03` → `rechnung-2025-03`)
- Modal-Close robuster (mousedown+mouseup match statt Drag-Kill), Escape schließt oberstes Modal
- Signaturen-Untermenü: Email-Templates (mit KI-Draft) + Adressbuch (mit Autocomplete)
- Zertifikate-Untermenü: self-signed X.509 erstellen oder PFX importieren, Export als .cer

**v1.7.22 — DB-Resilienz**
- SQLite WAL + busy_timeout, MigrateAsync-Retry mit 6× 2 s
- Friendly 503 mit Retry-After für transient DB-Fehler
- SqliteRecoveryMiddleware klärt Pool + retried GETs

**v1.8.0 – v1.8.5 — Datenbank-Backend flexibel**
- **DB-Wizard** unter Einstellungen → Datenbank: Azure-SQL Server + Login-Angabe (oder Raw Connection String), Ziel-DB automatisch anlegen, optionale Daten-Migration in FK-safer Reihenfolge, Restart, Selbst-Poll bis App zurück ist
- DbConfigStore in `/data/nimshare-dbconfig.json` — persistente Provider-Wahl über Restarts
- **Echte SqlServer-Migrations** (`NimShare.Migrations.SqlServer` Assembly) — kein EnsureCreated mehr, MigrateAsync auf beiden Providern
- EnableRetryOnFailure für SqlServer, Connection-Timeout 45 s
- Live-AI-Modell-Liste: Button holt echte Provider-Liste (Gemini/OpenAI/Anthropic/Azure) — Ende der kuratierten „vielleicht fehlen Modelle"-Falle
- Reassign / Weiterleiten für Signaturen (DocuSign-inspiriert), i18n aller Empfänger-Mails
- Gemini-Key aus URL (jetzt `x-goog-api-key` Header)
- Cert-500 mit robusterem Export (Linux-Container-Fallback via CopyWithPrivateKey), Fehler lokalisiert
- SSRF-Guard für Azure-OpenAI-Endpoint (nur `*.openai.azure.com` / `*.cognitiveservices.azure.com`)
- Signature-PNG DoS-Cap (2.5 MB Base64)
- Explizite Transaction um Reassign-Multi-Row-Change

## Nächste Meilensteine

### v1.9.x
- **iOS Parity nachziehen**: Wiki, Tokens, Webhooks, Versions, Locks, Bulk-Zip, Signatur-Reassign
- **Bulk-Send Signaturen** (CSV → N Envelopes, aus einem Template + Adressbuch)
- **Signer-Attachment-Feld**: „bitte Ausweis-Kopie hochladen" beim Unterschreiben
- **Initials-Feld** als eigener SignatureFieldType (kleine Signatur pro Seite)
- **File-Locks/Versions UI im Browser** — Ctx-Menu, Locks-Anzeige, Version-History-Modal

### v2.0
- **Actual PDF-Signing** mit Zertifikat-Integration (bisher nur Cert-Management, kein CAdES/PAdES)
- **WebDAV-Endpoint** (rclone, Windows-Netzlaufwerk, Finder)
- **Certificate of Completion** als separates Audit-PDF (nicht mehr embedded)
- **Signing-Groups**: Rolle statt Person („jemand aus Legal") mit Delegation

### Langfristig (kein Datum)
- **Custom Metadata + Views** (Notion-artige Spalten/Filter über Files)
- **Desktop-Sync-Client** (Windows/mac)
- **Android-App**
- **Virtual Drive / Cloud Mount**
- **E2E-verschlüsselte Libraries**
- **SAML/OIDC generisch** + LDAP-Sync
- **Realtime-WebSocket-Notifications**
- **Server-side Virus-Scanning-Hook**

## Kleinigkeiten (irgendwann)

- File-Versions Retention Job (KeepVersions automatisch prunen)
- Trash 30-Tage-Retention wirklich enforcen
- Admin Audit-Log CSV/JSON-Export
- Postmarks-Signed-Webhook-Retries (wenn Endpoint down)
- API-Doc-Refresh (OpenAPI mit den neuen Endpoints)

## Was wir haben, was Seafile nicht hat

- **AI-Suite**: Summary + Vision + Chat (RAG) + Draft-Emails + Risk-Detection + Semantic Search + OCR
- **Signaturen** mit Reassign + Zertifikate + KI-drafted Templates + Adressbuch
- **DB-Wahl im laufenden Betrieb** (SQLite → Azure SQL Wizard)
- **Native iOS-App**
- **Landing-Templates** pro User + global
- **Direct-Share** mit Ordner-Vererbung
- **Ordner-Icons** pro Ordner (Emoji + Farbe)
- **Öffentliche Admin-kuratierte Links**
- **Live-Provider-Model-List** für den AI-Gateway
