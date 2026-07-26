# Subdomain Sharing (v1.11.0)

Lets a user share a file or folder as `https://wichtig.nimshare.com` instead
of the classic `https://nimshare.com/s/wichtig`. Distinct from
[Custom Domains](CUSTOM_DOMAINS.md): custom domains bind a whole hostname to
one owner; subdomain sharing is per-**link**, on **one shared wildcard
domain** that any authorized user can mint a slug on.

## Architecture

- **Wildcard DNS**: `*.{BaseDomain}` (e.g. `*.nimshare.com`) → the App
  Service origin host, as a Cloudflare-proxied CNAME. Cloudflare Universal
  SSL covers first-level wildcards, so there's **no certificate to renew**
  on the App Service side — Cloudflare terminates TLS at the edge.
- **No API calls at request time.** `SubdomainShareMiddleware` (registered
  before `UseRouting`) reads the `Host` header, strips `{BaseDomain}`, looks
  the remaining label up against `ShareLink.SubdomainSlug` /
  `UploadRequestLink.SubdomainSlug` (in-process, `SubdomainShareService`
  caches the instance settings row for 60s), and rewrites
  `HttpContext.Request.Path` to `/s/{slug}` or `/u/{slug}`. Every existing
  landing feature (password, expiry, gallery mode, signer badge, GPS map)
  keeps working unmodified — there's zero duplicated logic.
- **Cloudflare is only touched by the setup assistant** (`/settings/subdomains`
  → "DNS automatisch einrichten"), which upserts the wildcard CNAME and an
  optional `asuid.{BaseDomain}` TXT record for the Azure custom-domain
  verification. The API token (Zone.DNS edit scope) is DataProtection-
  encrypted at rest and never round-tripped to the browser.

## Setup (admin, one-time)

1. Domain lives at Cloudflare, SSL/TLS mode = **Full** (not Flexible/Strict —
   Strict needs an origin cert you'd have to maintain yourself).
2. `/settings/subdomains`: enable, set `BaseDomain` (e.g. `nimshare.com`) and
   `OriginHost` (pre-filled from `WEBSITE_HOSTNAME`), optionally paste a
   Cloudflare API token with `Zone.DNS:Edit` on that zone, save.
3. Click **"DNS automatisch einrichten"** — creates/updates the wildcard
   CNAME (proxied) and, if `AzureVerificationId` is filled in, the
   `asuid.*` TXT record.
4. In the Azure Portal, add a Custom Domain `*.{BaseDomain}` to the App
   Service once (the TXT record from step 3 satisfies the ownership check
   automatically — no manual DNS round-trip needed).
5. Done. No manual certificate work, ever — Cloudflare renews Universal SSL
   on its own.

`BaseDomain` is a per-instance setting, not hardcoded — every NimShare
deployment picks its own.

## Per-user permission

Subdomain slugs are a shared, flat namespace (`wichtig.` is gone once
someone takes it) — so only users with `User.CanUseSubdomainShares = true`
(or Admins, who always can) see the option in the Share / Upload-Request
modal. Toggle it per user on `/settings/users/{id}` under "Subdomain-
Freigaben".

## Reserved slugs

`SubdomainShareService.Reserved` blocks infrastructure- and confusion-prone
labels: `www`, `api`, `app`, `admin`, `auth`, `login`, `mail`, `smtp`, `ftp`,
`cdn`, `static`, `dev`, `staging`, `status`, `health`, `ns1/ns2`, `asuid`,
and similar. Slugs are DNS-safe (`[a-z0-9-]`, 2–63 chars, no leading/
trailing hyphen) and unique across **both** `ShareLinks` and
`UploadRequests` — one namespace, one 409 on collision.

## Troubleshooting

- **404 on `wichtig.nimshare.com`** — either the feature is disabled
  instance-wide, or no active link has that `SubdomainSlug`. The middleware
  rewrites to a landing NotFound view either way (never a bare 404), so the
  branding still shows.
- **Browser cert warning** — SSL/TLS mode at Cloudflare isn't "Full" (or
  the wildcard CNAME isn't proxied, i.e. showing a grey cloud instead of
  orange). Fix either and it resolves within a minute, no re-issue needed.
- **"DNS automatisch einrichten" fails** — the API token lacks
  `Zone.DNS:Edit` on the target zone, or `BaseDomain` isn't actually hosted
  at Cloudflare under that account. The button reports the exact Cloudflare
  API error.
- **Slug rejected as "reserved" unexpectedly** — check
  `SubdomainShareService.Reserved`; extend the list there if a false
  positive shows up (e.g. a legitimately desired but common word).
