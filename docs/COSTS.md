# Costs

Indicative USD/month at low usage (EU-West).

| Component | SKU | Cost |
|---|---|---|
| App Service Plan | S1 | ~73 |
| Azure SQL DB | Basic (5 DTU) | ~5 |
| Storage Account | Standard LRS, 100 GB hot + 100 GB egress | ~6 |
| Application Insights | 5 GB ingestion | ~12 |
| Key Vault (optional) | Standard | ~1 |
| **Total** | | **~97** |

## Levers to shrink

- **Drop to B1** App Service Plan — ~55 USD; lose free managed certs, so custom domains need a self-signed cert or Front Door.
- **Serverless SQL** — auto-pauses at zero traffic; costs cents/day but adds a ~1 s cold start for the first request after idle.
- **Cool blobs** — lifecycle rule at 30 days sends older shares to Cool tier; halves storage cost for archival files.
- **Application Insights sampling** — set fixed-rate 20% in production; log volume shrinks 5×.
