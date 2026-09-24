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

// Dashboard sign-in through Microsoft Entra ID. Leave the authority and client id empty for the
// first deployment, then register the redirect URIs it prints; deploy.ps1 prompts for the secret.
param adminOidcAuthority = ''
param adminOidcClientId = ''
param adminOidcDisplayName = 'Microsoft Entra ID'
