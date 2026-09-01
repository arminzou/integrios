targetScope = 'resourceGroup'

param namespaceName string
param principalId string

var dataReceiverRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: namespaceName
}
resource receiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, principalId, dataReceiverRoleId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: dataReceiverRoleId
  }
}
