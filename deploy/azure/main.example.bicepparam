// `deploy.ps1` combines these nonsecret choices with separately supplied secure parameters.
using none

param namePrefix = 'integriosref'
param location = 'canadacentral'

param registryName = 'myregistry'
param registryResourceGroupName = 'rg-container-images'
param adminImage = 'myregistry.azurecr.io/integrios/admin@sha256:0000000000000000000000000000000000000000000000000000000000000000'
param ingestionImage = 'myregistry.azurecr.io/integrios/ingestion@sha256:0000000000000000000000000000000000000000000000000000000000000000'
param workerImage = 'myregistry.azurecr.io/integrios/worker@sha256:0000000000000000000000000000000000000000000000000000000000000000'

param databaseProvider = 'sqlserver'
param databaseAdministratorLogin = 'integrios_admin'

param adminAllowedCidrs = [
  '203.0.113.10/32'
]
param ingestionExternal = true

// Supply both values to grant Ingestion receiver access on an existing namespace.
// The reference never provisions a namespace, queue, topic, or subscription.
param serviceBusNamespaceName = ''
param serviceBusResourceGroupName = ''

param mappingTenantSlug = 'example'
param sourceReference = 'webhook_hmac'
param destinationReference = 'destination_api_key'
param mappingRevision = 'v1'
