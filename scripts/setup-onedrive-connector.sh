#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────
#  NimShare — OneDrive-Konnektor komplett automatisch einrichten
# ─────────────────────────────────────────────────────────────────────────
#  Erzeugt eine Entra-App-Registration, setzt Redirect-URI + Delegated-
#  Permissions, generiert ein Client-Secret, und trägt die drei Werte
#  als App-Settings in dein Azure App Service ein. Ein Restart triggert
#  den Live-Deploy.
#
#  Alternative zum UI-Wizard unter /settings/connectors — wenn du lieber
#  die Command-Line bevorzugst.
#
#  Voraussetzungen:  Azure CLI (az) mit `az login` als User mit Rechten
#                    zum Anlegen von App-Registrations UND als
#                    Contributor/Owner auf der App-Service-Resource.
#
#  Aufruf:  ./scripts/setup-onedrive-connector.sh <resource-group> <app-service-name> [redirect-host]
#  Beispiel: ./scripts/setup-onedrive-connector.sh nimshare-rg nimshare nimshare.com
# ─────────────────────────────────────────────────────────────────────────
set -euo pipefail

RG="${1:?resource-group missing}"
APP="${2:?app-service-name missing}"
HOST="${3:-${APP}.azurewebsites.net}"
REDIRECT="https://${HOST}/settings/connectors/onedrive/callback"
DISPLAY_NAME="NimShare Connectors"

echo "→ Creating Entra app registration '${DISPLAY_NAME}'…"
APP_JSON=$(az ad app create \
  --display-name "${DISPLAY_NAME}" \
  --sign-in-audience "AzureADandPersonalMicrosoftAccount" \
  --web-redirect-uris "${REDIRECT}" \
  --enable-id-token-issuance false \
  -o json)
APP_ID=$(echo "$APP_JSON" | grep -o '"appId": "[^"]*"' | head -1 | cut -d'"' -f4)
echo "   AppId (Client-ID): ${APP_ID}"

echo "→ Adding delegated Graph permissions (Files.Read + User.Read + offline_access)…"
# Microsoft Graph resource ID = 00000003-0000-0000-c000-000000000046
# Scope IDs:
#   Files.Read       = df85f4d6-205c-4ac5-a5ea-6bf408dba283
#   User.Read        = e1fe6dd8-ba31-4d61-89e7-88639da4683d
#   offline_access   = 7427e0e9-2fba-42fe-b0c0-848c9e6a8182
az ad app permission add \
  --id "${APP_ID}" \
  --api "00000003-0000-0000-c000-000000000046" \
  --api-permissions \
    "df85f4d6-205c-4ac5-a5ea-6bf408dba283=Scope" \
    "e1fe6dd8-ba31-4d61-89e7-88639da4683d=Scope" \
    "7427e0e9-2fba-42fe-b0c0-848c9e6a8182=Scope" \
  >/dev/null

echo "→ Generating client secret (24 month validity)…"
SECRET=$(az ad app credential reset \
  --id "${APP_ID}" \
  --years 2 \
  --display-name "NimShare-Setup-$(date +%Y%m%d)" \
  --query password -o tsv)

echo "→ Writing App Service configuration…"
az webapp config appsettings set \
  --resource-group "${RG}" \
  --name "${APP}" \
  --settings \
    "Connectors__OneDrive__ClientId=${APP_ID}" \
    "Connectors__OneDrive__ClientSecret=${SECRET}" \
    "Connectors__OneDrive__Tenant=common" \
  >/dev/null

echo "→ Restarting App Service…"
az webapp restart --resource-group "${RG}" --name "${APP}" >/dev/null

echo ""
echo "✓ Done."
echo "   Client-ID:  ${APP_ID}"
echo "   Tenant:     common"
echo "   Redirect:   ${REDIRECT}"
echo ""
echo "   Users can now visit https://${HOST}/settings/connectors and click"
echo "   'Connect OneDrive Business'. Secret rotation: run this script again"
echo "   or use the Admin UI (values there override App Service settings)."
echo ""
echo "   Note: users may see a Microsoft consent prompt for the three delegated"
echo "   permissions on first connect. That's expected."
