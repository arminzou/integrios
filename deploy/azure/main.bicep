targetScope = 'resourceGroup'

@description('Short lowercase deployment name used in resource names.')
@minLength(3)
@maxLength(16)
param namePrefix string

param location string = resourceGroup().location
param registryName string
param registryResourceGroupName string
param serviceBusNamespaceName string = ''
param serviceBusResourceGroupName string = ''

@allowed([
  'sqlserver'
  'postgres'
])
param databaseProvider string

@description('Full immutable ACR image reference ending in @sha256:<digest>.')
param adminImage string

@description('Full immutable ACR image reference ending in @sha256:<digest>.')
param ingestionImage string

@description('Full immutable ACR image reference ending in @sha256:<digest>.')
param workerImage string

@secure()
param operatorKeySecret string
@minLength(1)
param adminAllowedCidrs array
param ingestionExternal bool = true

@description('OpenID Connect issuer for Operator dashboard sign-in, for example https://login.microsoftonline.com/<tenant-id>/v2.0. Leave empty to deploy without dashboard sign-in.')
param adminOidcAuthority string = ''
param adminOidcClientId string = ''
param adminOidcDisplayName string = ''

@secure()
param adminOidcClientSecret string = ''

@allowed([0, 1])
param runtimeReplicaCount int = 0

var collectorImage = 'docker.io/otel/opentelemetry-collector-contrib@sha256:8164eab2e6bca9c9b0837a8d2f118a6618489008a839db7f9d6510e66be3923c'
var collectorAppName = '${'$'}{env:CONTAINER_APP_NAME}'
var collectorReplicaName = '${'$'}{env:CONTAINER_APP_REPLICA_NAME}'
var metricsEndpoint = '${telemetryEndpoint.properties.metricsIngestion.endpoint}/dataCollectionRules/${telemetryRule.properties.immutableId}/streams/Custom-Metrics-Otel/otlp/v1/metrics'
var tracesEndpoint = '${telemetryEndpoint.properties.logsIngestion.endpoint}/dataCollectionRules/${telemetryRule.properties.immutableId}/streams/Microsoft-OTLP-Traces/otlp/v1/traces'
var collectorConfigTemplate = '''
extensions:
  azure_auth:
    managed_identity:
      client_id: ${env:AZURE_CLIENT_ID}
    scopes:
      - https://monitor.azure.com/.default
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: localhost:4317
      http:
        endpoint: localhost:4318
  prometheus:
    config:
      scrape_configs:
        - job_name: integrios
          scrape_interval: 15s
          static_configs:
            - targets: [localhost:5299]
processors:
  resource:
    attributes:
      - key: container_app_name
        value: "__CONTAINER_APP_NAME__"
        action: upsert
      - key: container_app_replica_name
        value: "__CONTAINER_APP_REPLICA_NAME__"
        action: upsert
  cumulativetodelta: {}
  batch: {}
exporters:
  otlp_http/azuremonitor:
    traces_endpoint: __TRACES_ENDPOINT__
    metrics_endpoint: __METRICS_ENDPOINT__
    auth:
      authenticator: azure_auth
service:
  extensions: [azure_auth]
  pipelines:
    traces:
      receivers: [otlp]
      processors: [resource, batch]
      exporters: [otlp_http/azuremonitor]
    metrics:
      receivers: [prometheus]
      processors: [resource, cumulativetodelta, batch]
      exporters: [otlp_http/azuremonitor]
'''
var collectorConfig = replace(replace(replace(replace(collectorConfigTemplate, '__CONTAINER_APP_NAME__', collectorAppName), '__CONTAINER_APP_REPLICA_NAME__', collectorReplicaName), '__TRACES_ENDPOINT__', tracesEndpoint), '__METRICS_ENDPOINT__', metricsEndpoint)
var telemetryEnvironment = [
  { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: 'http://localhost:4317' }
  { name: 'OTEL_TRACES_SAMPLER', value: 'parentbased_always_on' }
]
var collectorContainer = {
  name: 'collector'
  image: collectorImage
  args: ['--config=env:OTEL_CONFIG']
  env: [{ name: 'OTEL_CONFIG', value: collectorConfig }]
  resources: { cpu: json('0.25'), memory: '0.5Gi' }
}
// The sidecar authenticates to Azure Monitor as its app's user-assigned identity; the apps carry no
// system-assigned identity.
var adminCollectorContainer = union(collectorContainer, {
  env: concat(collectorContainer.env, [{ name: 'AZURE_CLIENT_ID', value: adminIdentity.properties.clientId }])
})
var ingestionCollectorContainer = union(collectorContainer, {
  env: concat(collectorContainer.env, [{ name: 'AZURE_CLIENT_ID', value: ingestionIdentity.properties.clientId }])
})
var workerCollectorContainer = union(collectorContainer, {
  env: concat(collectorContainer.env, [{ name: 'AZURE_CLIENT_ID', value: workerIdentity.properties.clientId }])
})

var appNames = {
  admin: '${namePrefix}-admin'
  ingestion: '${namePrefix}-ingestion'
  worker: '${namePrefix}-worker'
}
var jobNames = {
  migrate: '${namePrefix}-migrate'
  grantRuntime: '${namePrefix}-grant'
  bootstrap: '${namePrefix}-bootstrap'
  validateSecrets: '${namePrefix}-validate'
}
var registryServer = '${registryName}.azurecr.io'
var useSqlServer = databaseProvider == 'sqlserver'
var serviceBusEnabled = !empty(serviceBusNamespaceName) && !empty(serviceBusResourceGroupName)
var adminDataProtectionPath = '/var/lib/integrios/data-protection'
var adminOidcEnabled = !empty(adminOidcAuthority)
var adminOidcSecretName = 'oidc-client-secret'
// Each workload reaches the database as its own managed identity; nothing carries a password.
func databaseEnvironment(provider string, serverFqdn string, identityName string, identityClientId string) array => concat(
  [
    { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
    { name: 'Database__Provider', value: provider }
  ],
  provider == 'sqlserver'
    ? [
        {
          name: 'ConnectionStrings__SqlServer'
          value: 'Server=tcp:${serverFqdn},1433;Initial Catalog=integrios;Persist Security Info=False;Authentication=Active Directory Managed Identity;User Id=${identityClientId};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
        }
      ]
    : [
        { name: 'Database__Postgres__Authentication', value: 'AzureEntra' }
        {
          name: 'ConnectionStrings__Postgres'
          value: 'Host=${serverFqdn};Port=5432;Database=integrios;Username=${identityName};SSL Mode=Require'
        }
      ],
  [{ name: 'AZURE_CLIENT_ID', value: identityClientId }])
var databaseServerFqdn = useSqlServer ? sqlServer!.properties.fullyQualifiedDomainName : postgres!.properties.fullyQualifiedDomainName
var adminDatabaseEnvironment = databaseEnvironment(databaseProvider, databaseServerFqdn, adminIdentity.name, adminIdentity.properties.clientId)
var ingestionDatabaseEnvironment = databaseEnvironment(databaseProvider, databaseServerFqdn, ingestionIdentity.name, ingestionIdentity.properties.clientId)
var workerDatabaseEnvironment = databaseEnvironment(databaseProvider, databaseServerFqdn, workerIdentity.name, workerIdentity.properties.clientId)
var migrateDatabaseEnvironment = databaseEnvironment(databaseProvider, databaseServerFqdn, migrateIdentity.name, migrateIdentity.properties.clientId)
var bootstrapDatabaseEnvironment = databaseEnvironment(databaseProvider, databaseServerFqdn, bootstrapIdentity.name, bootstrapIdentity.properties.clientId)
// Worker and the secret-validation job share Worker's identity and its Destination-secret vault.
var destinationSecretsEnvironment = [
  { name: 'Integrios__KeyVault__Uri', value: destinationSecretsVault.properties.vaultUri }
]
var keyVaultSecretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}'
  location: location
  properties: {
    retentionInDays: 30
    sku: { name: 'PerGB2018' }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource metrics 'Microsoft.Monitor/accounts@2023-04-03' = {
  name: 'amw-${namePrefix}'
  location: location
  properties: {}
}

resource telemetryEndpoint 'Microsoft.Insights/dataCollectionEndpoints@2024-03-11' = {
  name: 'dce-${namePrefix}'
  location: location
  properties: {
    description: 'Direct OTLP ingestion for Integrios traces and metrics'
    networkAcls: { publicNetworkAccess: 'Enabled' }
  }
}

resource telemetryRule 'Microsoft.Insights/dataCollectionRules@2024-03-11' = {
  name: 'dcr-${namePrefix}'
  location: location
  properties: {
    description: 'Routes Integrios traces to Application Insights and metrics to Azure Managed Prometheus'
    dataCollectionEndpointId: telemetryEndpoint.id
    references: {
      applicationInsights: [{
        resourceId: insights.id
        name: 'applicationInsights'
      }]
    }
    directDataSources: {
      otelMetrics: [{
        streams: ['Custom-Metrics-Otel']
        enrichWithResourceAttributes: ['*']
        enrichWithReference: 'applicationInsights'
        name: 'integriosMetrics'
      }]
      otelTraces: [{
        streams: [
          'Microsoft-OTel-Traces-Spans'
          'Microsoft-OTel-Traces-Events'
          'Microsoft-OTel-Traces-Resources'
        ]
        enrichWithResourceAttributes: ['*']
        enrichWithReference: 'applicationInsights'
        replaceResourceIdWithReference: true
        name: 'integriosTraces'
      }]
    }
    destinations: {
      monitoringAccounts: [{ accountResourceId: metrics.id, name: 'managedPrometheus' }]
      logAnalytics: [{ workspaceResourceId: logs.id, name: 'applicationInsightsWorkspace' }]
    }
    dataFlows: [
      { streams: ['Custom-Metrics-Otel'], destinations: ['managedPrometheus'] }
      {
        streams: [
          'Microsoft-OTel-Traces-Spans'
          'Microsoft-OTel-Traces-Events'
          'Microsoft-OTel-Traces-Resources'
        ]
        destinations: ['applicationInsightsWorkspace']
      }
    ]
  }
}

var workbookData = {
  version: 'Notebook/1.0'
  items: [
    {
      type: 1
      content: {
        json: '# Integrios operations\nUse these views to inspect outcomes, backlog, bounded failure classes, and one copied Admin `trace_id`.'
      }
      name: 'overview'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Ingestion and Delivery outcomes'
        query: string({
          version: 'PrometheusQueryProvider/1.0'
          customEndpoint: false
          queryText: 'sum by (signal) (label_replace(increase(integrios_events_ingested_total[1h]), "signal", "events_ingested", "__name__", ".*") or label_replace(increase(integrios_events_unrouted_total[1h]), "signal", "events_unrouted", "__name__", ".*") or label_replace(increase(integrios_fanout_rows_created_total[1h]), "signal", "fanout_rows_created", "__name__", ".*") or label_replace(increase(integrios_deliveries_succeeded_total[1h]), "signal", "deliveries_succeeded", "__name__", ".*") or label_replace(increase(integrios_deliveries_failed_total[1h]), "signal", "deliveries_failed", "__name__", ".*") or label_replace(increase(integrios_deliveries_dead_lettered_total[1h]), "signal", "deliveries_dead_lettered", "__name__", ".*"))'
          type: 'query_range'
        })
        size: 0
        timeContext: { durationMs: 86400000 }
        queryType: 16
        resourceType: 'microsoft.monitor/accounts'
        crossComponentResources: [metrics.id]
        visualization: 'timechart'
      }
      name: 'outcomes'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Backlog and staleness'
        query: string({
          version: 'PrometheusQueryProvider/1.0'
          customEndpoint: false
          queryText: 'max by (__name__) (integrios_outbox_pending_depth or on(__name__) integrios_outbox_oldest_pending_age_seconds or on(__name__) integrios_delivery_ready_depth or on(__name__) integrios_delivery_oldest_ready_age_seconds or on(__name__) integrios_backlog_snapshot_age_seconds)'
          type: 'query_range'
        })
        size: 0
        timeContext: { durationMs: 86400000 }
        queryType: 16
        resourceType: 'microsoft.monitor/accounts'
        crossComponentResources: [metrics.id]
        visualization: 'timechart'
      }
      name: 'backlog'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Failures and dead letters by Connector class'
        query: string({
          version: 'PrometheusQueryProvider/1.0'
          customEndpoint: false
          queryText: 'sum by (signal, connector_key) (label_replace(increase(integrios_deliveries_failed_total[1h]), "signal", "failed", "__name__", ".*") or label_replace(increase(integrios_deliveries_dead_lettered_total[1h]), "signal", "dead_lettered", "__name__", ".*"))'
          type: 'query_range'
        })
        size: 0
        timeContext: { durationMs: 86400000 }
        queryType: 16
        resourceType: 'microsoft.monitor/accounts'
        crossComponentResources: [metrics.id]
        visualization: 'timechart'
      }
      name: 'failures'
    }
    {
      type: 9
      content: {
        version: 'KqlParameterItem/1.0'
        parameters: [{
          id: '940e66ee-f4db-45b4-a268-17b713715cb5'
          version: 'KqlParameterItem/1.0'
          name: 'TraceId'
          label: 'Admin trace_id'
          type: 1
          isRequired: true
          value: ''
        }]
        style: 'pills'
        queryType: 0
      }
      name: 'trace-parameter'
    }
    {
      type: 3
      content: {
        version: 'KqlItem/1.0'
        title: 'Event trace lookup'
        query: 'OTelSpans | where TraceId == "{TraceId}" | project TimeGenerated, ServiceName, Name, Kind, StatusCode, TraceId, SpanId | order by TimeGenerated asc'
        size: 0
        timeContext: { durationMs: 2592000000 }
        queryType: 0
        resourceType: 'microsoft.operationalinsights/workspaces'
        crossComponentResources: [logs.id]
      }
      name: 'trace-lookup'
    }
  ]
  fallbackResourceIds: [insights.id]
}

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, 'integrios-operations')
  location: location
  kind: 'shared'
  properties: {
    displayName: 'Integrios Operations'
    serializedData: string(workbookData)
    version: '1.0'
    sourceId: insights.id
    category: 'workbook'
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: 'cae-${namePrefix}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    zoneRedundant: false
  }
}

// Container Apps mounts Azure Files over SMB with the account key, and this reference has no VNet,
// so the account accepts the key from any network. Anyone holding the key can read Admin's key
// ring and forge Operator sessions; restricting it needs a VNet-integrated environment. Azure
// Storage encrypts the share at rest by default.
resource adminDataProtectionStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${take(toLower(replace(namePrefix, '-', '')), 12)}${take(uniqueString(resourceGroup().id, 'data-protection'), 8)}'
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource adminDataProtectionFileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: adminDataProtectionStorage
  name: 'default'
}

resource adminDataProtectionShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: adminDataProtectionFileService
  name: 'admin-data-protection'
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 5
  }
}

resource adminDataProtectionEnvironmentStorage 'Microsoft.App/managedEnvironments/storages@2025-07-01' = {
  parent: environment
  name: 'admin-data-protection'
  properties: {
    azureFile: {
      accountName: adminDataProtectionStorage.name
      accountKey: adminDataProtectionStorage.listKeys().keys[0].value
      accessMode: 'ReadWrite'
      shareName: adminDataProtectionShare.name
    }
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${take(namePrefix, 10)}-${take(uniqueString(resourceGroup().id), 8)}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
    sku: { family: 'A', name: 'standard' }
  }
}

// Tenant secrets live in one vault per direction, apart from the deployment settings above. Each
// runtime process loads every secret in its own vault at startup through the Key Vault
// configuration source, so a vault must hold only that direction's SourceSecrets--* or
// DestinationSecrets--* values.
resource sourceSecretsVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${take(namePrefix, 8)}-src-${take(uniqueString(resourceGroup().id), 6)}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
    sku: { family: 'A', name: 'standard' }
  }
}

resource destinationSecretsVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${take(namePrefix, 8)}-dst-${take(uniqueString(resourceGroup().id), 6)}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
    sku: { family: 'A', name: 'standard' }
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = if (!useSqlServer) {
  name: 'pg-${namePrefix}-${uniqueString(resourceGroup().id)}'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: subscription().tenantId
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
    storage: {
      autoGrow: 'Disabled'
      storageSizeGB: 32
    }
  }
}

resource postgresDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = if (!useSqlServer) {
  parent: postgres
  name: 'integrios'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// V1 deliberately has no VNet. This Azure-only firewall rule is the small public-network bridge
// between Container Apps and PostgreSQL; Entra authentication and TLS protect database access.
resource postgresAzureServicesFirewall 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = if (!useSqlServer) {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = if (useSqlServer) {
  name: 'sql-${take(namePrefix, 12)}-${take(uniqueString(resourceGroup().id), 8)}'
  location: location
  properties: {
    // Entra-only servers take their administrator here and carry no SQL login.
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: migrateIdentity.name
      sid: migrateIdentity.properties.principalId
      tenantId: subscription().tenantId
      principalType: 'Application'
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    version: '12.0'
  }
}

// The server's inline setting applies only at creation; this resource turns SQL logins back off
// on every deployment if someone re-enabled them.
resource sqlServerEntraOnlyAuthentication 'Microsoft.Sql/servers/azureADOnlyAuthentications@2023-08-01-preview' = if (useSqlServer) {
  parent: sqlServer
  name: 'Default'
  properties: { azureADOnlyAuthentication: true }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (useSqlServer) {
  parent: sqlServer
  name: 'integrios'
  location: location
  sku: {
    name: 'GP_S_Gen5_1'
    tier: 'GeneralPurpose'
    capacity: 1
  }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}

// V1 deliberately has no VNet. This Azure-only firewall rule is the small public-network bridge
// between Container Apps and Azure SQL; Entra authentication and TLS protect database access.
resource sqlAzureServicesFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (useSqlServer) {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource operatorKeySecretResource 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'operator-key-bootstrap'
  properties: { value: operatorKeySecret }
}

resource adminOidcClientSecretResource 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (adminOidcEnabled) {
  parent: vault
  name: 'admin-oidc-client-secret'
  properties: { value: adminOidcClientSecret }
}

resource adminIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-admin'
  location: location
}
resource ingestionIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-ingestion'
  location: location
}
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-worker'
  location: location
}
resource migrateIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-migrate'
  location: location
}
resource bootstrapIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-bootstrap'
  location: location
}

module acrPull 'acr-pull.bicep' = {
  name: 'acr-pull'
  scope: resourceGroup(registryResourceGroupName)
  params: {
    registryName: registryName
    principalIds: [
      adminIdentity.properties.principalId
      ingestionIdentity.properties.principalId
      workerIdentity.properties.principalId
      migrateIdentity.properties.principalId
      bootstrapIdentity.properties.principalId
    ]
  }
}

resource adminOidcSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (adminOidcEnabled) {
  scope: adminOidcClientSecretResource
  name: guid(adminOidcClientSecretResource.id, adminIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: adminIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}
resource bootstrapOperatorSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: operatorKeySecretResource
  name: guid(operatorKeySecretResource.id, bootstrapIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: bootstrapIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}

// Only Ingestion reads Source secrets; only Worker, and the secret-validation job that runs as
// Worker's identity, read Destination secrets.
resource ingestionSourceSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sourceSecretsVault
  name: guid(sourceSecretsVault.id, ingestionIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: ingestionIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}
resource workerDestinationSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: destinationSecretsVault
  name: guid(destinationSecretsVault.id, workerIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: workerIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}

resource ingestion 'Microsoft.App/containerApps@2025-07-01' = {
  name: appNames.ingestion
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${ingestionIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: ingestionExternal
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: [{ server: registryServer, identity: ingestionIdentity.id }]
      secrets: []
    }
    template: {
      containers: [{
        name: 'ingestion'
        image: ingestionImage
        env: concat(ingestionDatabaseEnvironment, [
          { name: 'Integrios__KeyVault__Uri', value: sourceSecretsVault.properties.vaultUri }
        ], telemetryEnvironment)
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: '/health', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/ready', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 10 }
        ]
      }, ingestionCollectorContainer]
      scale: { minReplicas: runtimeReplicaCount, maxReplicas: max(runtimeReplicaCount, 1) }
    }
  }
  dependsOn: [acrPull, ingestionSourceSecrets]
}

module serviceBusReceiver 'service-bus-receiver.bicep' = if (serviceBusEnabled) {
  name: 'service-bus-receiver'
  scope: resourceGroup(serviceBusResourceGroupName)
  params: {
    namespaceName: serviceBusNamespaceName
    principalId: ingestionIdentity.properties.principalId
  }
}

resource admin 'Microsoft.App/containerApps@2025-07-01' = {
  name: appNames.admin
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${adminIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        ipSecurityRestrictions: [for (cidr, index) in adminAllowedCidrs: {
          name: 'operator-${index}'
          description: 'Operator-supplied Admin CIDR'
          action: 'Allow'
          ipAddressRange: cidr
        }]
      }
      registries: [{ server: registryServer, identity: adminIdentity.id }]
      secrets: adminOidcEnabled
        ? [{ name: adminOidcSecretName, keyVaultUrl: adminOidcClientSecretResource!.properties.secretUriWithVersion, identity: adminIdentity.id }]
        : []
    }
    template: {
      containers: [{
        name: 'admin'
        image: adminImage
        env: concat(adminDatabaseEnvironment, [
          { name: 'Integrios__PublicIngestionBaseUri', value: 'https://${ingestion.properties.configuration.ingress.fqdn}' }
          { name: 'Integrios__Admin__DataProtection__KeyRingPath', value: adminDataProtectionPath }
          // Ingress terminates TLS, so Admin must read the original https scheme to build its
          // OpenID Connect callback. Ingress is the only path to the container.
          { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        ], adminOidcEnabled ? [
          { name: 'Integrios__Admin__Oidc__Authority', value: adminOidcAuthority }
          { name: 'Integrios__Admin__Oidc__ClientId', value: adminOidcClientId }
          { name: 'Integrios__Admin__Oidc__DisplayName', value: adminOidcDisplayName }
          { name: 'Integrios__Admin__Oidc__ClientSecret', secretRef: adminOidcSecretName }
        ] : [], telemetryEnvironment)
        volumeMounts: [{ volumeName: 'admin-data-protection', mountPath: adminDataProtectionPath }]
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: '/health', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/ready', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 10 }
        ]
      }, adminCollectorContainer]
      volumes: [{ name: 'admin-data-protection', storageType: 'AzureFile', storageName: adminDataProtectionEnvironmentStorage.name }]
      scale: { minReplicas: runtimeReplicaCount, maxReplicas: max(runtimeReplicaCount, 1) }
    }
  }
  dependsOn: [acrPull, adminOidcSecret]
}

resource worker 'Microsoft.App/containerApps@2025-07-01' = {
  name: appNames.worker
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${workerIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: registryServer, identity: workerIdentity.id }]
      secrets: []
    }
    template: {
      containers: [{
        name: 'worker'
        image: workerImage
        // Completed-history retention is intentionally absent. Add
        // Integrios__Worker__HistoryRetention__Period only after reviewing the rollout warning.
        env: concat(workerDatabaseEnvironment, destinationSecretsEnvironment, telemetryEnvironment)
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: '/health', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/ready', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 10 }
        ]
      }, workerCollectorContainer]
      scale: { minReplicas: runtimeReplicaCount, maxReplicas: max(runtimeReplicaCount, 1) }
    }
  }
  dependsOn: [acrPull, workerDestinationSecrets]
}

resource migrateJob 'Microsoft.App/jobs@2025-07-01' = {
  name: jobNames.migrate
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${migrateIdentity.id}': {} } }
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registryServer, identity: migrateIdentity.id }]
      secrets: []
    }
    template: {
      containers: [{
        name: 'migrate'
        image: adminImage
        args: ['database', 'migrate']
        env: migrateDatabaseEnvironment
        resources: { cpu: json('0.5'), memory: '1Gi' }
      }]
    }
  }
  dependsOn: [acrPull, sqlServerEntraOnlyAuthentication]
}

resource grantRuntimeJob 'Microsoft.App/jobs@2025-07-01' = {
  name: jobNames.grantRuntime
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${migrateIdentity.id}': {} } }
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registryServer, identity: migrateIdentity.id }]
      secrets: []
    }
    template: {
      containers: [{
        name: 'grant-runtime'
        image: adminImage
        args: ['database', 'grant-runtime']
        env: concat(migrateDatabaseEnvironment, [
          { name: 'Database__RuntimePrincipals__0__Name', value: adminIdentity.name }
          { name: 'Database__RuntimePrincipals__0__Scope', value: 'control-plane' }
          { name: 'Database__RuntimePrincipals__0__EntraClientId', value: adminIdentity.properties.clientId }
          { name: 'Database__RuntimePrincipals__0__EntraObjectId', value: adminIdentity.properties.principalId }
          { name: 'Database__RuntimePrincipals__1__Name', value: bootstrapIdentity.name }
          { name: 'Database__RuntimePrincipals__1__Scope', value: 'control-plane' }
          { name: 'Database__RuntimePrincipals__1__EntraClientId', value: bootstrapIdentity.properties.clientId }
          { name: 'Database__RuntimePrincipals__1__EntraObjectId', value: bootstrapIdentity.properties.principalId }
          { name: 'Database__RuntimePrincipals__2__Name', value: ingestionIdentity.name }
          { name: 'Database__RuntimePrincipals__2__Scope', value: 'data-plane' }
          { name: 'Database__RuntimePrincipals__2__EntraClientId', value: ingestionIdentity.properties.clientId }
          { name: 'Database__RuntimePrincipals__2__EntraObjectId', value: ingestionIdentity.properties.principalId }
          { name: 'Database__RuntimePrincipals__3__Name', value: workerIdentity.name }
          { name: 'Database__RuntimePrincipals__3__Scope', value: 'data-plane' }
          { name: 'Database__RuntimePrincipals__3__EntraClientId', value: workerIdentity.properties.clientId }
          { name: 'Database__RuntimePrincipals__3__EntraObjectId', value: workerIdentity.properties.principalId }
        ])
        resources: { cpu: json('0.5'), memory: '1Gi' }
      }]
    }
  }
  dependsOn: [acrPull, migrateJob]
}

resource bootstrapJob 'Microsoft.App/jobs@2025-07-01' = {
  name: jobNames.bootstrap
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${bootstrapIdentity.id}': {} } }
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registryServer, identity: bootstrapIdentity.id }]
      secrets: [
        { name: 'operator-key', keyVaultUrl: operatorKeySecretResource.properties.secretUriWithVersion, identity: bootstrapIdentity.id }
      ]
    }
    template: {
      containers: [{
        name: 'bootstrap'
        image: adminImage
        args: ['bootstrap']
        env: concat(bootstrapDatabaseEnvironment, [
          { name: 'INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET', secretRef: 'operator-key' }
        ])
        resources: { cpu: json('0.5'), memory: '1Gi' }
      }]
    }
  }
  dependsOn: [acrPull, bootstrapOperatorSecret, grantRuntimeJob]
}

resource validateSecretsJob 'Microsoft.App/jobs@2025-07-01' = {
  name: jobNames.validateSecrets
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity.id}': {} } }
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registryServer, identity: workerIdentity.id }]
      secrets: []
    }
    template: {
      containers: [{
        name: 'validate'
        image: workerImage
        args: ['secrets', 'validate', '--all']
        env: concat(workerDatabaseEnvironment, destinationSecretsEnvironment)
        resources: { cpu: json('0.5'), memory: '1Gi' }
      }]
    }
  }
  dependsOn: [acrPull, workerDestinationSecrets, grantRuntimeJob]
}

module telemetryPublisher 'telemetry-publisher.bicep' = {
  name: 'telemetry-publisher'
  params: {
    dataCollectionRuleName: telemetryRule.name
    principalIds: [
      adminIdentity.properties.principalId
      ingestionIdentity.properties.principalId
      workerIdentity.properties.principalId
    ]
  }
}

output adminFqdn string = admin.properties.configuration.ingress.fqdn
output databaseServerName string = useSqlServer ? sqlServer!.name : postgres!.name
output migrateIdentityName string = migrateIdentity.name
output migrateIdentityPrincipalId string = migrateIdentity.properties.principalId
output adminOidcRedirectUris object = {
  callback: 'https://${admin.properties.configuration.ingress.fqdn}/auth/callback'
  signedOut: 'https://${admin.properties.configuration.ingress.fqdn}/auth/signed-out'
}
output ingestionFqdn string = ingestion.properties.configuration.ingress.fqdn
output deploymentSettingsVault string = vault.name
output secretVaults object = {
  source: sourceSecretsVault.name
  destination: destinationSecretsVault.name
}
output appNames object = appNames
output jobNames object = jobNames
output monitoring object = {
  applicationInsightsId: insights.id
  azureMonitorWorkspaceId: metrics.id
  dataCollectionEndpointId: telemetryEndpoint.id
  dataCollectionRuleId: telemetryRule.id
  logAnalyticsWorkspaceId: logs.id
  workbookId: workbook.id
}
