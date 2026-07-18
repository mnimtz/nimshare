# Local development setup

## Prerequisites

- .NET 8 SDK
- Docker (for Azurite storage emulator)
- An Entra ID (Azure AD) tenant you can create an app registration in — free personal tenant works.

## 1. Entra ID app registration

1. [portal.azure.com](https://portal.azure.com) → Microsoft Entra ID → App registrations → **New registration**.
2. Name: `NimShare Dev`. Supported account types: **Accounts in this organizational directory only** (or Multitenant + personal Microsoft accounts if you want personal MSA sign-in too).
3. Redirect URI (Web): `https://localhost:5099/signin-oidc`
4. Click **Register**. Copy the **Application (client) ID** and the **Directory (tenant) ID**.
5. Authentication → Front-channel logout URL: `https://localhost:5099/signout-callback-oidc`. Save.
6. Certificates & secrets → New client secret → copy the value.

## 2. Configure local secrets

From the `src/NimShare.Api` folder:

```bash
dotnet user-secrets set "AzureAd:TenantId" "<your-tenant-guid>"
dotnet user-secrets set "AzureAd:ClientId" "<your-client-id>"
dotnet user-secrets set "AzureAd:ClientSecret" "<your-client-secret>"
```

## 3. Start Azurite (blob emulator)

```bash
docker run -d --name azurite -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite
```

## 4. Run

```bash
dotnet run --project src/NimShare.Api
```

Open <https://localhost:5099>. On first sign-in, the app auto-creates a `User` row for you.

## Skipping auth for UI work

If you're just tweaking CSS on the public share landing (`/s/{slug}`) or the welcome page, you don't need Entra. Both routes are `[AllowAnonymous]` and render without a real user.
