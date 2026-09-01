targetScope = 'resourceGroup'

param registryName string
param principalIds array

var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource assignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in principalIds: {
  scope: registry
  name: guid(registry.id, principalId, acrPullRoleId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleId
  }
}]
