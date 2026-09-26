// `deploy.ps1` combines these nonsecret choices with separately supplied secure parameters.
using none

param namePrefix = 'integriosref'
param location = 'canadacentral'

param registryName = 'myregistry'
param registryResourceGroupName = 'rg-container-images'
// deploy.ps1 imports this release into the registry when absent and deploys it pinned by digest.
// To deploy images you built yourself, remove release and set adminImage, ingestionImage, and
// workerImage to full <registry>.azurecr.io/<repository>@sha256:<digest> references instead.
param release = '0.9.0' // x-release-please-version

param databaseProvider = 'sqlserver'

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
