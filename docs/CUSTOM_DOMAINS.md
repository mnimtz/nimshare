# Custom Domains

## Prerequisites

- App Service Plan is **Standard S1** or higher (needed for custom hostnames + free Managed Certificates).
- The user owns the DNS zone they want to use.

## User flow (Settings → Domains)

1. **Add.** User enters `share.example.com`.
2. **Get verification token.** NimShare shows:
   > Add these DNS records with your provider, then click Verify:
   >
   > | Host                          | Type  | Value                              |
   > |-------------------------------|-------|------------------------------------|
   > | `_nimshare-verify.share`      | TXT   | `4KV7-ZQFB-9M2X-…`                 |
   > | `share`                       | CNAME | `<siteName>.azurewebsites.net`     |
3. **Verify.** User adds the records at their DNS provider, waits for propagation, clicks *Verify*. NimShare's `DomainVerifierService` polls TXT records for up to 30 minutes.
4. **Bind.** On success, NimShare uses the App Service management API to add the hostname to the site.
5. **Certificate.** NimShare requests a free App Service Managed Certificate; usually issued within a minute after CNAME resolution is stable.
6. **Live.** User can now share links as `https://share.example.com/s/…`.

## What happens when a request lands

Middleware `CustomDomainMiddleware`:

1. Look up the `Host` header in `CustomDomain` where `VerificationStatus = Verified`.
2. If found, set `HttpContext.Items["OwnerId"] = CustomDomain.OwnerId` and continue.
3. If `Host` matches the default `*.azurewebsites.net` name or a configured primary domain, no owner is bound (default routing).

Slug lookup remains **global** — the same slug resolves regardless of hostname. The middleware exists so that the landing page can render the *right* owner branding (avatar, message defaults, custom sender name in the notification email footer).

## Removing a domain

`DELETE /api/v1/custom-domains/{id}`:

1. Marks row as `Deleted`.
2. Calls App Service management API to remove the hostname binding.
3. The Managed Certificate is auto-revoked by Azure once the binding is gone.

## Limits

- Max 5 custom domains per user by default (`Domains:MaxPerUser` in appsettings).
- Wildcard domains (`*.example.com`) are not supported by App Service Managed Certificates — those users need to bring their own cert into Key Vault. Out of MVP scope.
