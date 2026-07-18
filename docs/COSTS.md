# Costs

Indicative EUR/month at low usage (EU-West), matching the defaults in `infra/azuredeploy.json`.

| Component | SKU | Cost |
|---|---|---|
| App Service Plan | B1 (Linux) | ~10 |
| Storage Account | Standard LRS, 100 GB Blob hot + 5 GB File share | ~2 |
| **Total** | | **~12 EUR/month** |

The metadata DB is SQLite persisted on the mounted Azure Files share — no separate managed SQL bill.

## Levers to shrink

- **Drop to F1** App Service Plan — free tier, but the app sleeps when idle (first hit takes ~10 s) and there's no always-on for the notification scheduler.
- **Cool blobs** — lifecycle rule at 30 days sends older shares to Cool tier; halves storage cost for archival files.

## Levers to scale up

- **Upgrade to S1** if you want:
  - Custom domains per user with free App Service Managed Certificates
  - Deployment slots for staging
  - ~ 60 EUR/month
- **Swap SQLite for Azure SQL** if you go past ~5 concurrent writers or want multi-region replication. Set `Database__Provider=SqlServer` and point `ConnectionStrings__Default` at your Azure SQL instance.
- **Add Application Insights** — set `APPLICATIONINSIGHTS_CONNECTION_STRING`; auto-instruments requests/deps/exceptions.
