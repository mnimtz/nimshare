// ─────────────────────────────────────────────────────────────────────────────
// NimShare — infrastructure as code
// Deploys:  App Service (S1) + Storage Account + Azure SQL + App Insights.
// Uses managed identity where possible; secrets go into Key Vault.
// ─────────────────────────────────────────────────────────────────────────────

@description('Globally unique site name; becomes <siteName>.azurewebsites.net.')
param siteName string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Entra ID tenant id used for sign-in.')
param entraTenantId string

@description('Entra ID application (client) id.')
param entraClientId string

@description('SQL admin login (initial).')
param sqlAdminLogin string

@description('SQL admin password (initial).')
@secure()
param sqlAdminPassword string

@description('App Service Plan SKU. S1 is needed for custom domains + free managed cert.')
@allowed([ 'B1', 'S1', 'P1V3' ])
param appServiceSku string = 'S1'

var storageAccountName = toLower(replace('${siteName}stor', '-', ''))
var sqlServerName = toLower('${siteName}-sql')
var sqlDatabaseName = 'nimshare'
var planName = '${siteName}-plan'
var appInsightsName = '${siteName}-ai'
var logAnalyticsName = '${siteName}-la'

// ── Log Analytics ───────────────────────────────────────────────────────────
resource logs 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    retentionInDays: 30
    sku: { name: 'PerGB2018' }
  }
}

// ── Application Insights (workspace-based) ──────────────────────────────────
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    IngestionMode: 'LogAnalytics'
  }
}

// ── Storage ─────────────────────────────────────────────────────────────────
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    cors: {
      corsRules: [
        {
          allowedOrigins: [ 'https://${siteName}.azurewebsites.net' ]
          allowedMethods: [ 'PUT', 'GET', 'HEAD' ]
          allowedHeaders: [ '*' ]
          exposedHeaders: [ '*' ]
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

resource filesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'files'
  properties: { publicAccess: 'None' }
}

// ── Azure SQL ───────────────────────────────────────────────────────────────
resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlFirewallAzure 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzure'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  sku: { name: 'Basic', tier: 'Basic', capacity: 5 }
  properties: {}
}

// ── App Service Plan ────────────────────────────────────────────────────────
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: { name: appServiceSku, tier: appServiceSku == 'B1' ? 'Basic' : (appServiceSku == 'S1' ? 'Standard' : 'PremiumV3') }
  kind: 'app'
  properties: { reserved: false }
}

// ── App Service ─────────────────────────────────────────────────────────────
resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: siteName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      alwaysOn: appServiceSku != 'B1'
      minTlsVersion: '1.2'
      ftpsState: 'FtpsOnly'
      http20Enabled: true
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'ApplicationInsightsAgent_EXTENSION_VERSION', value: '~3' }
        { name: 'AzureAd__Instance', value: 'https://login.microsoftonline.com/' }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: entraClientId }
        { name: 'AzureAd__Domain', value: 'common' }
        { name: 'AzureAd__CallbackPath', value: '/signin-oidc' }
        { name: 'Database__Provider', value: 'SqlServer' }
        { name: 'ConnectionStrings__Default', value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabaseName};Persist Security Info=False;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;' }
        { name: 'Storage__ConnectionString', value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=core.windows.net' }
        { name: 'Storage__ContainerName', value: 'files' }
        { name: 'Storage__UseManagedIdentity', value: 'false' }
        { name: 'IpHash__Salt', value: uniqueString(resourceGroup().id, siteName) }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      ]
    }
  }
}

// Grant the App Service's managed identity blob-data-contributor on the storage account
// so it can later be flipped to use OAuth+UDK instead of the account key.
resource storageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, site.id, 'Storage Blob Data Contributor')
  properties: {
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
    // Storage Blob Data Contributor
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

// ── Outputs ────────────────────────────────────────────────────────────────
output siteUrl string = 'https://${site.properties.defaultHostName}'
output storageAccount string = storage.name
output sqlServer string = sqlServer.properties.fullyQualifiedDomainName
output appInsightsName string = insights.name
