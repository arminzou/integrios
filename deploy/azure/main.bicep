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
param databaseAdministratorPassword string

@secure()
param operatorKeySecret string

@secure()
param sourceSecretValue string

@secure()
param destinationSecretValue string

param databaseAdministratorLogin string = 'integrios_admin'
param adminAllowedCidrs array
param ingestionExternal bool = true
param mappingTenantSlug string
param sourceReference string
param destinationReference string
param mappingRevision string

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
    managed_identity: {}
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

var appNames = {
  admin: '${namePrefix}-admin'
  ingestion: '${namePrefix}-ingestion'
  worker: '${namePrefix}-worker'
}
var jobNames = {
  migrate: '${namePrefix}-migrate'
  bootstrap: '${namePrefix}-bootstrap'
  validateSecrets: '${namePrefix}-validate'
}
var registryServer = '${registryName}.azurecr.io'
var useSqlServer = databaseProvider == 'sqlserver'
var serviceBusEnabled = !empty(serviceBusNamespaceName) && !empty(serviceBusResourceGroupName)
var databaseConnection = useSqlServer
  ? 'Server=tcp:${sqlServer!.properties.fullyQualifiedDomainName},1433;Initial Catalog=integrios;Persist Security Info=False;User ID=${databaseAdministratorLogin};Password=${databaseAdministratorPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
  : 'Host=${postgres!.properties.fullyQualifiedDomainName};Port=5432;Database=integrios;Username=${databaseAdministratorLogin};Password=${databaseAdministratorPassword};SSL Mode=Require'
var databaseSecretName = 'database-connection'
var databaseEnvironment = [
  { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
  { name: 'Database__Provider', value: databaseProvider }
  { name: useSqlServer ? 'ConnectionStrings__SqlServer' : 'ConnectionStrings__Postgres', secretRef: databaseSecretName }
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
          queryText: 'sum by (__name__) (increase(integrios_events_ingested_total[1h]) or on(__name__) increase(integrios_events_unrouted_total[1h]) or on(__name__) increase(integrios_fanout_rows_created_total[1h]) or on(__name__) increase(integrios_deliveries_succeeded_total[1h]) or on(__name__) increase(integrios_deliveries_failed_total[1h]) or on(__name__) increase(integrios_deliveries_dead_lettered_total[1h]))'
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
          queryText: 'sum by (__name__, connector_key) (increase(integrios_deliveries_failed_total[1h]) or on(__name__) increase(integrios_deliveries_dead_lettered_total[1h]))'
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

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = if (!useSqlServer) {
  name: 'pg-${namePrefix}-${uniqueString(resourceGroup().id)}'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: databaseAdministratorLogin
    administratorLoginPassword: databaseAdministratorPassword
    version: '16'
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
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
// between Container Apps and PostgreSQL; credentials and TLS still protect the database.
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
    administratorLogin: databaseAdministratorLogin
    administratorLoginPassword: databaseAdministratorPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    version: '12.0'
  }
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
// between Container Apps and Azure SQL; credentials and TLS still protect the database.
resource sqlAzureServicesFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (useSqlServer) {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource databaseConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: databaseSecretName
  properties: { value: databaseConnection }
}

resource operatorKeySecretResource 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'operator-key-bootstrap'
  properties: { value: operatorKeySecret }
}

resource sourceSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'source-${mappingTenantSlug}-${replace(sourceReference, '_', '-')}'
  properties: { value: sourceSecretValue }
}

resource destinationSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'destination-${mappingTenantSlug}-${replace(destinationReference, '_', '-')}'
  properties: { value: destinationSecretValue }
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

resource adminDatabaseSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: databaseConnectionSecret
  name: guid(databaseConnectionSecret.id, adminIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: adminIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}
resource ingestionDatabaseSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: databaseConnectionSecret
  name: guid(databaseConnectionSecret.id, ingestionIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: ingestionIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}
resource workerDatabaseSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: databaseConnectionSecret
  name: guid(databaseConnectionSecret.id, workerIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: workerIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}
resource migrateDatabaseSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: databaseConnectionSecret
  name: guid(databaseConnectionSecret.id, migrateIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: migrateIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}
resource bootstrapDatabaseSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: databaseConnectionSecret
  name: guid(databaseConnectionSecret.id, bootstrapIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: bootstrapIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}
resource bootstrapOperatorSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: operatorKeySecretResource
  name: guid(operatorKeySecretResource.id, bootstrapIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: bootstrapIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}

resource ingestionSourceSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sourceSecret
  name: guid(sourceSecret.id, ingestionIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: ingestionIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}
resource workerDestinationSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: destinationSecret
  name: guid(destinationSecret.id, workerIdentity.id, keyVaultSecretsUserRoleId)
  properties: { principalId: workerIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: keyVaultSecretsUserRoleId }
}

resource ingestion 'Microsoft.App/containerApps@2025-07-01' = {
  name: appNames.ingestion
  location: location
  identity: {
    type: 'SystemAssigned,UserAssigned'
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
      secrets: [
        { name: databaseSecretName, keyVaultUrl: databaseConnectionSecret.properties.secretUriWithVersion, identity: ingestionIdentity.id }
        { name: 'source-${mappingRevision}', keyVaultUrl: sourceSecret.properties.secretUriWithVersion, identity: ingestionIdentity.id }
      ]
    }
    template: {
      containers: [{
        name: 'ingestion'
        image: ingestionImage
        env: concat(databaseEnvironment, [
          { name: 'Integrios__SourceSecrets__Provider', value: 'configuration' }
          { name: 'SourceSecrets__${mappingTenantSlug}__${sourceReference}', secretRef: 'source-${mappingRevision}' }
        ], telemetryEnvironment)
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: '/health', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/ready', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 10 }
        ]
      }, collectorContainer]
      scale: { minReplicas: runtimeReplicaCount, maxReplicas: max(runtimeReplicaCount, 1) }
    }
  }
  dependsOn: [acrPull, ingestionDatabaseSecret, ingestionSourceSecret]
}

module serviceBusReceiver 'service-bus-receiver.bicep' = if (serviceBusEnabled) {
  name: 'service-bus-receiver'
  scope: resourceGroup(serviceBusResourceGroupName)
  params: {
    namespaceName: serviceBusNamespaceName
    principalId: ingestion.identity.principalId
  }
}

resource admin 'Microsoft.App/containerApps@2025-07-01' = {
  name: appNames.admin
  location: location
  identity: {
    type: 'SystemAssigned,UserAssigned'
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
      secrets: [{ name: databaseSecretName, keyVaultUrl: databaseConnectionSecret.properties.secretUriWithVersion, identity: adminIdentity.id }]
    }
    template: {
      containers: [{
        name: 'admin'
        image: adminImage
        env: concat(databaseEnvironment, [
          { name: 'Integrios__PublicIngestionBaseUri', value: 'https://${ingestion.properties.configuration.ingress.fqdn}' }
        ], telemetryEnvironment)
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: '/health', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/ready', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 10 }
        ]
      }, collectorContainer]
      scale: { minReplicas: runtimeReplicaCount, maxReplicas: max(runtimeReplicaCount, 1) }
    }
  }
  dependsOn: [acrPull, adminDatabaseSecret]
}

resource worker 'Microsoft.App/containerApps@2025-07-01' = {
  name: appNames.worker
  location: location
  identity: {
    type: 'SystemAssigned,UserAssigned'
    userAssignedIdentities: { '${workerIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: registryServer, identity: workerIdentity.id }]
      secrets: [
        { name: databaseSecretName, keyVaultUrl: databaseConnectionSecret.properties.secretUriWithVersion, identity: workerIdentity.id }
        { name: 'destination-${mappingRevision}', keyVaultUrl: destinationSecret.properties.secretUriWithVersion, identity: workerIdentity.id }
      ]
    }
    template: {
      containers: [{
        name: 'worker'
        image: workerImage
        env: concat(databaseEnvironment, [
          { name: 'Integrios__DestinationSecrets__Provider', value: 'configuration' }
          { name: 'DestinationSecrets__${mappingTenantSlug}__${destinationReference}', secretRef: 'destination-${mappingRevision}' }
        ], telemetryEnvironment)
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: '/health', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/ready', port: 5299, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 10 }
        ]
      }, collectorContainer]
      scale: { minReplicas: runtimeReplicaCount, maxReplicas: max(runtimeReplicaCount, 1) }
    }
  }
  dependsOn: [acrPull, workerDatabaseSecret, workerDestinationSecret]
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
      secrets: [{ name: databaseSecretName, keyVaultUrl: databaseConnectionSecret.properties.secretUriWithVersion, identity: migrateIdentity.id }]
    }
    template: {
      containers: [{
        name: 'migrate'
        image: adminImage
        args: ['database', 'migrate']
        env: databaseEnvironment
        resources: { cpu: json('0.5'), memory: '1Gi' }
      }]
    }
  }
  dependsOn: [acrPull, migrateDatabaseSecret]
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
        { name: databaseSecretName, keyVaultUrl: databaseConnectionSecret.properties.secretUriWithVersion, identity: bootstrapIdentity.id }
        { name: 'operator-key', keyVaultUrl: operatorKeySecretResource.properties.secretUriWithVersion, identity: bootstrapIdentity.id }
      ]
    }
    template: {
      containers: [{
        name: 'bootstrap'
        image: adminImage
        args: ['bootstrap']
        env: concat(databaseEnvironment, [
          { name: 'INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET', secretRef: 'operator-key' }
        ])
        resources: { cpu: json('0.5'), memory: '1Gi' }
      }]
    }
  }
  dependsOn: [acrPull, bootstrapDatabaseSecret, bootstrapOperatorSecret]
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
      secrets: [
        { name: databaseSecretName, keyVaultUrl: databaseConnectionSecret.properties.secretUriWithVersion, identity: workerIdentity.id }
        { name: 'destination-${mappingRevision}', keyVaultUrl: destinationSecret.properties.secretUriWithVersion, identity: workerIdentity.id }
      ]
    }
    template: {
      containers: [{
        name: 'validate'
        image: workerImage
        args: ['secrets', 'validate', '--all']
        env: concat(databaseEnvironment, [
          { name: 'Integrios__DestinationSecrets__Provider', value: 'configuration' }
          { name: 'DestinationSecrets__${mappingTenantSlug}__${destinationReference}', secretRef: 'destination-${mappingRevision}' }
        ])
        resources: { cpu: json('0.5'), memory: '1Gi' }
      }]
    }
  }
  dependsOn: [acrPull, workerDatabaseSecret, workerDestinationSecret]
}

module telemetryPublisher 'telemetry-publisher.bicep' = {
  name: 'telemetry-publisher'
  params: {
    dataCollectionRuleName: telemetryRule.name
    principalIds: [
      admin.identity.principalId
      ingestion.identity.principalId
      worker.identity.principalId
    ]
  }
}

output adminFqdn string = admin.properties.configuration.ingress.fqdn
output ingestionFqdn string = ingestion.properties.configuration.ingress.fqdn
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
