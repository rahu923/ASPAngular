targetScope = 'resourceGroup'

@description('Unique lowercase app name; this creates a paid Linux plan and staging slot.')
param appName string
param location string = resourceGroup().location
param acrName string
@description('Existing RBAC-enabled vault in this resource group. Secret values are never passed to Bicep.')
param keyVaultName string
@description('Existing delegated Microsoft.Web/serverFarms subnet with connectivity/DNS for private SQL/Key Vault.')
param integrationSubnetId string
@minLength(1)
@description('Verified immediate App Service proxy network(s) in CIDR form. Do not use 0.0.0.0/0.')
param trustedProxyNetworks array
@minLength(1)
@description('Private release-agent outbound CIDRs allowed to reach the staging slot.')
param stagingAllowedCidrs array
param imageTag string = 'bootstrap'
param smtpHost string
param smtpFrom string
param smtpUsername string
param seqServerUrl string
@description('Map config names to versionless Key Vault secret URIs. See example parameters.')
param vaultReferences object
param tags object = { application: 'order-management' }

var productionOrigin = 'https://${appName}.azurewebsites.net'
var stagingOrigin = 'https://${appName}-staging.azurewebsites.net'
var baseSettings = {
  ASPNETCORE_ENVIRONMENT: 'Production'
  ASPNETCORE_URLS: 'http://+:8080'
  ASPNETCORE_HTTPS_PORT: '443'
  ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'false'
  WEBSITES_PORT: '8080'
  WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'true'
  WEBSITE_HEALTHCHECK_MAXPINGFAILURES: '3'
  WEBSITE_SWAP_WARMUP_PING_PATH: '/api/health/ready'
  WEBSITE_SWAP_WARMUP_PING_STATUSES: '200'
  AllowedHosts: '${appName}.azurewebsites.net;${appName}-staging.azurewebsites.net'
  Observability__Console__Enabled: 'true'
  Observability__File__Enabled: 'true'
  Observability__File__Path: '/home/LogFiles/ordermanagement/api-.json'
  Observability__Seq__Enabled: 'true'
  Observability__Seq__ServerUrl: seqServerUrl
  Observability__SqlServer__Enabled: 'true'
  Smtp__Host: smtpHost
  Smtp__Port: '587'
  Smtp__From: smtpFrom
  Smtp__Username: smtpUsername
  CatalogCache__Enabled: 'false'
}
var proxySettings = toObject(range(0, length(trustedProxyNetworks)), i => 'ReverseProxy__KnownNetworks__${i}', i => trustedProxyNetworks[i])
var secrets = toObject(items(vaultReferences), entry => entry.key, entry => '@Microsoft.KeyVault(SecretUri=${entry.value})')
// These values are secret REFERENCES. App Service resolves them using each slot's managed
// identity; no JWT key/password needs to be embedded in the template or deployment outputs.
var settings = union(baseSettings, proxySettings, secrets)

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: { adminUserEnabled: false }
}
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${appName}-plan'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: 'P1v3', tier: 'PremiumV3', capacity: 1 }
  properties: { reserved: true }
}
resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  tags: tags
  kind: 'app,linux,container'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    virtualNetworkSubnetId: integrationSubnetId
    siteConfig: {
      linuxFxVersion: 'DOCKER|${registry.properties.loginServer}/orders:${imageTag}'
      acrUseManagedIdentityCreds: true
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/api/health/ready'
      vnetRouteAllEnabled: true
      appSettings: [for setting in items(union(settings, { PublicOrigin: productionOrigin })): {name: setting.key, value: string(setting.value)}]
    }
  }
}
resource staging 'Microsoft.Web/sites/slots@2023-12-01' = {
  parent: web
  name: 'staging'
  location: location
  kind: 'app,linux,container'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    virtualNetworkSubnetId: integrationSubnetId
    siteConfig: {
      linuxFxVersion: 'DOCKER|${registry.properties.loginServer}/orders:${imageTag}'
      acrUseManagedIdentityCreds: true
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/api/health/ready'
      vnetRouteAllEnabled: true
      ipSecurityRestrictionsDefaultAction: 'Deny'
      ipSecurityRestrictions: [for (cidr, i) in stagingAllowedCidrs: {
        name: 'release-agent-${i}'
        ipAddress: cidr
        action: 'Allow'
        priority: 100 + i
      }]
      appSettings: [for setting in items(union(settings, { PublicOrigin: stagingOrigin })): {name: setting.key, value: string(setting.value)}]
    }
  }
}
resource stickySettings 'Microsoft.Web/sites/config@2023-12-01' = {
  // Keep origins/secrets with their target slot when swapping application versions.
  parent: web
  name: 'slotConfigNames'
  properties: { appSettingNames: concat(['PublicOrigin', 'AllowedHosts'], map(items(vaultReferences), entry => entry.key)) }
}
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, web.id, 'AcrPull')
  scope: registry
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}
resource stagingAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, staging.id, 'AcrPull')
  scope: registry
  properties: {
    principalId: staging.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}
resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, web.id, 'SecretsUser')
  scope: vault
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}
resource stagingSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, staging.id, 'SecretsUser')
  scope: vault
  properties: {
    principalId: staging.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}
resource disableScmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: web
  name: 'scm'
  properties: { allow: false }
}
resource disableFtpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: web
  name: 'ftp'
  properties: { allow: false }
}
resource disableStagingScmBasicAuth 'Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: staging
  name: 'scm'
  properties: { allow: false }
}
resource disableStagingFtpBasicAuth 'Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: staging
  name: 'ftp'
  properties: { allow: false }
}
output registryLoginServer string = registry.properties.loginServer
output productionUrl string = productionOrigin
output stagingUrl string = stagingOrigin
