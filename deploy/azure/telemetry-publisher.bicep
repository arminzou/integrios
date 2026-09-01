targetScope = 'resourceGroup'

param dataCollectionRuleName string
param principalIds array

var monitoringMetricsPublisherRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')

resource dataCollectionRule 'Microsoft.Insights/dataCollectionRules@2024-03-11' existing = {
  name: dataCollectionRuleName
}

resource assignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in principalIds: {
  scope: dataCollectionRule
  name: guid(dataCollectionRule.id, principalId, monitoringMetricsPublisherRoleId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: monitoringMetricsPublisherRoleId
  }
}]
