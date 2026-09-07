targetScope = 'resourceGroup'

@allowed([
  'dev'
  'prod'
])
param environment string
param location string = resourceGroup().location
param webLocation string = 'centralus'
param sqlLocation string = 'westus2'

@secure()
param sqlAdministratorPassword string
param sqlAdministratorLogin string = 'auralyadmin'
param sqlEntraAdministratorLogin string
param sqlEntraAdministratorObjectId string

@description('Shared Azure OpenAI/Foundry endpoint. No AI resource is created by this template.')
param sharedOpenAiEndpoint string
param sharedOpenAiResourceGroupName string
param sharedOpenAiAccountName string
param textModelDeploymentName string = 'gpt-4.1-mini'
param audioModelDeploymentName string = 'whisper'

@secure()
param jwtSecret string
@secure()
param offlineLeaseSigningPrivateKeyPem string
param webPushPublicKey string
@secure()
param webPushPrivateKey string
@secure()
param fiscalSecretProtectionKey string
@secure()
param whatsAppVerifyToken string
param whatsAppApiBaseUrl string = 'https://graph.facebook.com/v25.0/'

param releaseVersion string
@minLength(64)
@maxLength(64)
param posInstallerSha256 string
param deployStaticAdminSettings bool = true
param maximumFunctionInstances int = 20
param seedAppConfiguration bool = false

var suffix = toLower(take(uniqueString(subscription().id, environment), 8))
var compactEnvironment = environment == 'prod' ? 'prod' : 'dev'
var tags = {
  application: 'auraly'
  environment: compactEnvironment
  managedBy: 'bicep'
  release: releaseVersion
}

var storageName = 'stauraly${compactEnvironment}${suffix}'
var sqlServerName = 'sql-auraly-${compactEnvironment}-${suffix}'
var databaseName = 'auraly-${compactEnvironment}'
var identityName = 'id-auraly-${compactEnvironment}'
var appConfigurationName = 'cfg-auraly-${compactEnvironment}-${suffix}'
var serviceBusName = 'sb-auraly-${compactEnvironment}-${suffix}'
var webPubSubName = 'wps-auraly-${compactEnvironment}-${suffix}'
var functionName = 'func-auraly-${compactEnvironment}-${suffix}'
var apiName = 'api-auraly-${compactEnvironment}-${suffix}'
var adminName = 'admin-auraly-${compactEnvironment}-${suffix}'
var emailServiceName = 'email-auraly-${compactEnvironment}-${suffix}'
var communicationServiceName = 'acs-auraly-${compactEnvironment}-${suffix}'
var fiscalKeyVaultName = 'kv-auraly-${compactEnvironment}-${suffix}'

var blobDataOwnerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var queueDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var tableDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
var appConfigurationReaderRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '516239f1-63e1-4d78-a4de-a74fb236a071')
var appConfigurationOwnerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b')
var serviceBusSenderRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
var serviceBusReceiverRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
var webPubSubOwnerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '12cf5a90-567b-43ae-8102-96cf46c7d9b4')
var keyVaultCertificatesOfficerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'a4417e6f-fecd-4de8-b567-7b0420556985')
var keyVaultSecretsOfficerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}
resource fiscalKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: fiscalKeyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

resource fiscalKeyVaultCertificatesRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(fiscalKeyVault.id, identity.id, keyVaultCertificatesOfficerRoleId)
  scope: fiscalKeyVault
  properties: {
    roleDefinitionId: keyVaultCertificatesOfficerRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource fiscalKeyVaultSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(fiscalKeyVault.id, identity.id, keyVaultSecretsOfficerRoleId)
  scope: fiscalKeyVault
  properties: {
    roleDefinitionId: keyVaultSecretsOfficerRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
resource emailService 'Microsoft.Communication/emailServices@2025-09-01' = {
  name: emailServiceName
  location: 'global'
  tags: tags
  properties: {
    dataLocation: 'United States'
  }
}

resource emailDomain 'Microsoft.Communication/emailServices/domains@2025-09-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource communicationService 'Microsoft.Communication/communicationServices@2025-09-01' = {
  name: communicationServiceName
  location: 'global'
  tags: tags
  properties: {
    dataLocation: 'United States'
    linkedDomains: [
      emailDomain.id
    ]
  }
}

@description('Grants this environment access to the one shared Azure OpenAI account.')
module sharedOpenAiAccess './modules/shared-openai-access.bicep' = {
  name: 'shared-openai-access-${compactEnvironment}'
  scope: resourceGroup(sharedOpenAiResourceGroupName)
  params: {
    accountName: sharedOpenAiAccountName
    principalId: identity.properties.principalId
    identityResourceId: identity.id
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'function-releases'
  properties: {
    publicAccess: 'None'
  }
}

resource posInstallerContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'downloads'
  properties: {
    publicAccess: 'None'
  }
}
resource storageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, blobDataOwnerRoleId)
  scope: storage
  properties: {
    roleDefinitionId: blobDataOwnerRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, queueDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: queueDataContributorRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, tableDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: tableDataContributorRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-auraly-${compactEnvironment}'
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    workspaceCapping: {
      dailyQuotaGb: json('0.1')
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-auraly-${compactEnvironment}'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: false
    IngestionMode: 'LogAnalytics'
    RetentionInDays: 30
  }
}

resource appConfiguration 'Microsoft.AppConfiguration/configurationStores@2024-06-01' = {
  name: appConfigurationName
  location: location
  tags: tags
  sku: {
    name: 'free'
  }
  properties: {
    createMode: 'Default'
    disableLocalAuth: !seedAppConfiguration
    publicNetworkAccess: 'Enabled'
    dataPlaneProxy: {
      authenticationMode: seedAppConfiguration ? 'Local' : 'Pass-through'
      privateLinkDelegation: 'Disabled'
    }
  }
}

resource appConfigurationRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appConfiguration.id, identity.id, appConfigurationReaderRoleId)
  scope: appConfiguration
  properties: {
    roleDefinitionId: appConfigurationReaderRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource appConfigurationDeploymentOwnerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appConfiguration.id, sqlEntraAdministratorObjectId, appConfigurationOwnerRoleId)
  scope: appConfiguration
  properties: {
    roleDefinitionId: appConfigurationOwnerRoleId
    principalId: sqlEntraAdministratorObjectId
    principalType: 'User'
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    zoneRedundant: false
  }
}

resource inboundQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'whatsapp-inbound-debounce'
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P1D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    requiresSession: true
  }
}

resource campaignQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'campaign-dispatch'
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    requiresSession: true
  }
}

resource documentProcessingQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'auraly-document-processing'
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    requiresSession: true
  }
}

resource accountingProcessingQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'auraly-accounting-processing'
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    requiresSession: true
  }
}
resource fiscalProcessingQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'auraly-fiscal-processing'
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    requiresSession: true
  }
}

resource salesReportingQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'auraly-sales-reporting'
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    requiresSession: true
  }
}

resource serviceBusSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, identity.id, serviceBusSenderRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: serviceBusSenderRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource serviceBusReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, identity.id, serviceBusReceiverRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: serviceBusReceiverRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource webPubSub 'Microsoft.SignalRService/webPubSub@2024-03-01' = {
  name: webPubSubName
  location: location
  tags: tags
  sku: {
    name: environment == 'dev' ? 'Free_F1' : 'Standard_S1'
    tier: environment == 'dev' ? 'Free' : 'Standard'
    capacity: 1
  }
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    tls: {
      clientCertEnabled: false
    }
  }
}

resource webPubSubOwnerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webPubSub.id, identity.id, webPubSubOwnerRoleId)
  scope: webPubSub
  properties: {
    roleDefinitionId: webPubSubOwnerRoleId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: sqlLocation
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    administratorLogin: sqlAdministratorLogin
    administratorLoginPassword: sqlAdministratorPassword
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: false
      login: sqlEntraAdministratorLogin
      principalType: 'User'
      sid: sqlEntraAdministratorObjectId
      tenantId: subscription().tenantId
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
  }
}

// API and Function authenticate with the environment's user-assigned managed
// identity. Flex Consumption has dynamic egress, so enumerating its reported
// outbound IPs is not a complete or stable network rule.
resource azureServicesSqlFirewallRule 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: sqlLocation
  tags: tags
  sku: {
    name: environment == 'dev' ? 'Basic' : 'S1'
    tier: environment == 'dev' ? 'Basic' : 'Standard'
    capacity: environment == 'dev' ? 5 : 20
  }
  properties: {
    maxSizeBytes: environment == 'dev' ? 2147483648 : 268435456000
    readScale: 'Disabled'
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}

resource functionPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-func-auraly-${compactEnvironment}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: functionPlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainer.name}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumFunctionInstances
        instanceMemoryMB: 2048
        alwaysReady: []
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
    }
  }
  dependsOn: [
    storageBlobRole
    storageQueueRole
    storageTableRole
  ]
}

resource functionSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: {
    FUNCTIONS_EXTENSION_VERSION: '~4'
    AZURE_FUNCTIONS_ENVIRONMENT: environment == 'prod' ? 'Production' : 'Development'
    AzureWebJobsStorage__accountName: storage.name
    AzureWebJobsStorage__credential: 'managedidentity'
    AzureWebJobsStorage__clientId: identity.properties.clientId
    ServiceBusConnection__fullyQualifiedNamespace: '${serviceBus.name}.servicebus.windows.net'
    ServiceBusConnection__clientId: identity.properties.clientId
    Auraly__Accounting__ServiceBus__QueueName: accountingProcessingQueue.name
    Auraly__Fiscal__ServiceBus__QueueName: fiscalProcessingQueue.name
    Auraly__SalesReporting__ServiceBus__QueueName: salesReportingQueue.name
    AppConfiguration__Endpoint: appConfiguration.properties.endpoint
    AZURE_CLIENT_ID: identity.properties.clientId
    APPLICATIONINSIGHTS_CONNECTION_STRING: applicationInsights.properties.ConnectionString
    ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
    Release__Version: releaseVersion
    AURALY_ENVIRONMENT: compactEnvironment
    WhatsApp__Webhook__ApiBaseUrl: whatsAppApiBaseUrl
    WhatsApp__Webhook__VerifyToken: whatsAppVerifyToken
  }
}

resource apiPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-api-auraly-${compactEnvironment}'
  location: webLocation
  tags: tags
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource apiApp 'Microsoft.Web/sites@2024-04-01' = {
  name: apiName
  location: webLocation
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: apiPlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      alwaysOn: environment == 'prod'
      ftpsState: 'Disabled'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|8.0'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: environment == 'prod' ? 'Production' : 'Development'
        }
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storage.name
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: identity.properties.clientId
        }
        {
          name: 'ServiceBusConnection__fullyQualifiedNamespace'
          value: '${serviceBus.name}.servicebus.windows.net'
        }
        {
          name: 'ServiceBusConnection__clientId'
          value: identity.properties.clientId
        }
        {
          name: 'Auraly__DocumentProcessing__ServiceBus__QueueName'
          value: documentProcessingQueue.name
        }
        {
          name: 'Auraly__Accounting__ServiceBus__QueueName'
          value: accountingProcessingQueue.name
        }
        {
          name: 'Auraly__Fiscal__ServiceBus__QueueName'
          value: fiscalProcessingQueue.name
        }
        {
          name: 'Auraly__SalesReporting__ServiceBus__QueueName'
          value: salesReportingQueue.name
        }
        {
          name: 'Auraly__PosSynchronization__WebPubSub__Endpoint'
          value: 'https://${webPubSub.properties.hostName}'
        }
        {
          name: 'Auraly__PosSynchronization__WebPubSub__ManagedIdentityClientId'
          value: identity.properties.clientId
        }
        {
          name: 'Auraly__PosSynchronization__WebPubSub__Hub'
          value: 'auraly_pos'
        }
        {
          name: 'AppConfiguration__Endpoint'
          value: appConfiguration.properties.endpoint
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: identity.properties.clientId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsights.properties.ConnectionString
        }
        {
          name: 'Auraly__Environment'
          value: compactEnvironment
        }
        {
          name: 'Auraly__Fiscal__SecretProtectionKey'
          value: fiscalSecretProtectionKey
        }
        {
          name: 'Auraly__Fiscal__CredentialStore'
          value: 'AzureKeyVault'
        }
        {
          name: 'Auraly__Fiscal__KeyVaultUri'
          value: fiscalKeyVault.properties.vaultUri
        }
        {
          name: 'Authentication__Jwt__SigningKey'
          value: jwtSecret
        }
        {
          name: 'Authentication__Jwt__Issuer'
          value: 'auraly-${compactEnvironment}'
        }
        {
          name: 'Authentication__Jwt__Audience'
          value: 'auraly-admin-${compactEnvironment}'
        }
        {
          name: 'Authentication__OfflineLeaseSigning__KeyId'
          value: 'auraly-${compactEnvironment}-offline-v1'
        }
        {
          name: 'Authentication__OfflineLeaseSigning__PrivateKeyPem'
          value: offlineLeaseSigningPrivateKeyPem
        }
        {
          name: 'Authentication__OfflineLeaseSigning__DurationHours'
          value: '8'
        }
        {
          name: 'Notifications__WebPush__PublicKey'
          value: webPushPublicKey
        }
        {
          name: 'Notifications__WebPush__PrivateKey'
          value: webPushPrivateKey
        }
        {
          name: 'Notifications__WebPush__Subject'
          value: 'mailto:soporte@auraly.app'
        }
        {
          name: 'Notifications__WebPush__PublicAppUrl'
          value: environment == 'prod' ? 'https://auralyapp.co' : 'https://${staticAdmin.properties.defaultHostname}'
        }
        {
          name: 'WhatsApp__Webhook__ApiBaseUrl'
          value: whatsAppApiBaseUrl
        }
        {
          name: 'WhatsApp__Webhook__VerifyToken'
          value: whatsAppVerifyToken
        }
        {
          name: 'Auraly__Email__ConnectionString'
          value: communicationService.listKeys().primaryConnectionString
        }
        {
          name: 'Auraly__Email__SenderAddress'
          value: 'DoNotReply@${emailDomain.properties.mailFromSenderDomain}'
        }
        {
          name: 'Auraly__Email__PublicAppUrl'
          value: 'https://auralyapp.co'
        }
        {
          name: 'Auraly__Email__LogoUrl'
          value: 'https://auralyapp.co/brand/auraly-mark.png'
        }
        {
          name: 'Auraly__Email__SupportEmail'
          value: 'soporte@auralyapp.co'
        }
        {
          name: 'PosInstaller__ContainerName'
          value: posInstallerContainer.name
        }
        {
          name: 'PosInstaller__BlobName'
          value: 'Auraly-POS-Setup.exe'
        }
        {
          name: 'PosInstaller__Version'
          value: releaseVersion
        }
        {
          name: 'PosInstaller__Sha256'
          value: toUpper(posInstallerSha256)
        }
        {
          name: 'Release__Version'
          value: releaseVersion
        }
      ]
    }
  }
}

resource staticAdmin 'Microsoft.Web/staticSites@2023-12-01' = {
  name: adminName
  location: location
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    allowConfigFileUpdates: true
    enterpriseGradeCdnStatus: 'Disabled'
  }
}

resource staticAdminSettings 'Microsoft.Web/staticSites/config@2025-03-01' = if (deployStaticAdminSettings) {
  parent: staticAdmin
  name: 'appsettings'
  properties: {
    AURALY_API_URL: 'https://${apiApp.properties.defaultHostName}/api'
  }
}

resource environmentConfig 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-06-01' = if (seedAppConfiguration) {
  parent: appConfiguration
  name: 'Auraly:Environment'
  properties: {
    value: compactEnvironment
    contentType: 'text/plain'
  }
  dependsOn: [
    appConfigurationDeploymentOwnerRole
  ]
}

resource sqlConfig 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-06-01' = if (seedAppConfiguration) {
  parent: appConfiguration
  name: 'ConnectionStrings:DefaultConnection'
  properties: {
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${database.name};Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
    contentType: 'text/plain'
  }
  dependsOn: [
    appConfigurationDeploymentOwnerRole
  ]
}

resource auralySqlConfig 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-06-01' = if (seedAppConfiguration) {
  parent: appConfiguration
  name: 'ConnectionStrings:Auraly'
  properties: {
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${database.name};Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
    contentType: 'text/plain'
  }
  dependsOn: [
    appConfigurationDeploymentOwnerRole
  ]
}

resource openAiEndpointConfig 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-06-01' = if (seedAppConfiguration) {
  parent: appConfiguration
  name: 'OpenAI:TextModel:Endpoint'
  properties: {
    value: sharedOpenAiEndpoint
    contentType: 'text/plain'
  }
  dependsOn: [
    appConfigurationDeploymentOwnerRole
  ]
}

resource openAiTextDeploymentConfig 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-06-01' = if (seedAppConfiguration) {
  parent: appConfiguration
  name: 'OpenAI:TextModel:DeploymentName'
  properties: {
    value: textModelDeploymentName
    contentType: 'text/plain'
  }
  dependsOn: [
    appConfigurationDeploymentOwnerRole
  ]
}

resource openAiAudioEndpointConfig 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-06-01' = if (seedAppConfiguration) {
  parent: appConfiguration
  name: 'OpenAI:AudioModel:Endpoint'
  properties: {
    value: sharedOpenAiEndpoint
    contentType: 'text/plain'
  }
  dependsOn: [
    appConfigurationDeploymentOwnerRole
  ]
}

resource openAiAudioDeploymentConfig 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-06-01' = if (seedAppConfiguration) {
  parent: appConfiguration
  name: 'OpenAI:AudioModel:DeploymentName'
  properties: {
    value: audioModelDeploymentName
    contentType: 'text/plain'
  }
  dependsOn: [
    appConfigurationDeploymentOwnerRole
  ]
}

output functionAppName string = functionApp.name
output apiAppName string = apiApp.name
output staticAdminName string = staticAdmin.name
output sqlServerName string = sqlServer.name
output databaseName string = database.name
output serviceBusName string = serviceBus.name
output appConfigurationName string = appConfiguration.name
output managedIdentityName string = identity.name
output managedIdentityClientId string = identity.properties.clientId
output managedIdentityPrincipalId string = identity.properties.principalId
output fiscalKeyVaultName string = fiscalKeyVault.name
output fiscalKeyVaultUri string = fiscalKeyVault.properties.vaultUri
