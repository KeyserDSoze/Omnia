// Tao PaaS resource baseline; not production-safe until private networking and identity are added.
targetScope = 'resourceGroup'

@description('Short globally unique prefix (lowercase letters/numbers only).')
@minLength(4)
@maxLength(12)
param prefix string
param location string = resourceGroup().location

@secure()
@description('Strong PostgreSQL admin password, supplied from secure environment, never committed.')
param dbPassword string
@description('Admin username; do not use for runtime API connections after least-privilege setup.')
param dbAdmin string = 'taoadmin'

var suffix = uniqueString(resourceGroup().id, prefix)
var storageName = take(toLower('${prefix}${suffix}'), 24)

resource logs 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${prefix}-logs'
  location: location
  properties: { retentionInDays: 30 }
  sku: { name: 'PerGB2018' }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${prefix}${suffix}'
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource blobs 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-kv-${suffix}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Disabled'
  }
}

resource bus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${prefix}-sb-${suffix}'
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
  properties: { minimumTlsVersion: '1.2' }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${prefix}-pg-${suffix}'
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    administratorLogin: dbAdmin
    administratorLoginPassword: dbPassword
    version: '17'
    storage: { storageSizeGB: 32 }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    network: { publicNetworkAccess: 'Disabled' }
  }
}

resource pgVector 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: { value: 'vector', source: 'user-override' }
}

resource pgdb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'tao'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

resource containerEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-cae'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: listKeys(logs.id, logs.apiVersion).primarySharedKey
      }
    }
  }
}

output acrLoginServer string = registry.properties.loginServer
output postgresqlHostname string = postgres.properties.fullyQualifiedDomainName
output containerAppsEnvironmentId string = containerEnv.id
output storageAccountName string = blobs.name
output keyVaultUri string = vault.properties.vaultUri
