// ENPPI Workload Forecast - one environment (dev, uat or prod) on Azure.
//   az deployment group create -g <resource-group> -f main.bicep -p environment=uat entraClientId=<app id> \
//      sqlAdminGroupName=ENPPI-Workload-SQL-Admins sqlAdminGroupObjectId=<group object id>
// Then store the app registration's client secret in the app setting MICROSOFT_PROVIDER_AUTHENTICATION_SECRET (or move it
// to Key Vault), and run postdeploy.sql in the database as the Entra admin to grant the web app's managed identity access.

@allowed(['dev', 'uat', 'prod'])
param environment string = 'dev'
param location string = resourceGroup().location
@description('Short name used in resource names')
param appName string = 'enppi-workload'
@description('Application (client) ID of the Entra app registration used for sign-in')
param entraClientId string
param tenantId string = subscription().tenantId
@description('Entra group that administers the SQL server (Entra-only authentication, no SQL passwords)')
param sqlAdminGroupName string
param sqlAdminGroupObjectId string
param appServiceSku string = environment == 'prod' ? 'P1v3' : 'B1'

var name = '${appName}-${environment}'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 90 }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${name}-ai'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}

resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${name}-sql'
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'   // production: switch to a private endpoint and 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: tenantId
      principalType: 'Group'
    }
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sql
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: 'workload'
  location: location
  sku: environment == 'prod' ? { name: 'S2', tier: 'Standard' } : { name: 'S0', tier: 'Standard' }
  properties: { requestedBackupStorageRedundancy: 'Zone' }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${name}-plan'
  location: location
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: { reserved: true }
}

resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: '${name}-web'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: environment != 'dev'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'Database__Provider', value: 'SqlServer' }
        { name: 'ConnectionStrings__Workload', value: 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=workload;Authentication=Active Directory Managed Identity;Encrypt=True;' }
        { name: 'Auth__Mode', value: 'EasyAuth' }
        { name: 'Seed__Demo', value: 'false' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET', value: '' }   // set after deployment (or a Key Vault reference)
        { name: 'Notifications__Smtp__Host', value: '' }                  // ENPPI SMTP relay; empty = reminders are recorded only
      ]
    }
  }
}

// App Service Authentication: ENPPI Microsoft accounts only; the platform injects X-MS-CLIENT-PRINCIPAL for the API
resource auth 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: web
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
      excludedPaths: ['/health']
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: '${az.environment().authentication.loginEndpoint}${tenantId}/v2.0'
          clientId: entraClientId
          clientSecretSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
        }
        validation: { allowedAudiences: ['api://${entraClientId}', entraClientId] }
      }
    }
    login: { tokenStore: { enabled: true } }
  }
}

output webUrl string = 'https://${web.properties.defaultHostName}'
output webIdentityName string = web.name
output sqlServer string = sql.properties.fullyQualifiedDomainName
