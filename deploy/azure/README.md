# Azure Container Apps reference

This directory is a minimal copy-and-own reference for running one matched Integrios release on
Azure Container Apps. Copy it, review it, and adapt the Bicep to your networking, capacity,
availability, security, and operational requirements.

The reference deploys separate Admin, Ingestion, and Worker Container Apps; migration, Bootstrap,
and secret-validation jobs; one selected managed database; Key Vault; Log Analytics;
Application Insights; Azure Managed Prometheus; and an Operator Workbook. It reuses an existing
Azure Container Registry and never provisions Service Bus topology.

## Supplied defaults

| Area | Supplied reference |
|---|---|
| Runtime | One Admin, Ingestion, and Worker replica; no autoscaling or zone redundancy |
| Admin | External HTTPS restricted to explicit Operator CIDRs; OperatorKey authentication remains required |
| Ingestion | External HTTPS by default; may be internal independently of Service Bus access |
| Network | Public Container Apps environment, Key Vault, telemetry endpoints, and database firewall rules; no VNet or private endpoints |
| Azure SQL | General Purpose serverless, one vCore maximum, 0.5 minimum capacity, 60-minute auto-pause, local backup redundancy |
| PostgreSQL | PostgreSQL 16 Burstable B1ms, 32 GiB storage, seven-day local backup, no HA or geo-redundant backup |
| Deployment | Maintenance window: runtime at zero, migrate, Bootstrap, validate secrets, then start one replica |
| Images | Existing Operator-owned ACR and immutable Admin, Ingestion, and Worker digests from one release |
| Service Bus | Optional receiver role on an existing namespace; no namespace, queue, topic, or subscription creation |

## Prerequisites

- PowerShell 7 and Azure CLI with Bicep;
- an authenticated Azure subscription with the required resource providers registered;
- permission to create resources and role assignments in the deployment, ACR, and optional
  Service Bus resource groups;
- an existing ACR containing the matched Admin, Ingestion, and Worker images;
- a region available to both Container Apps and the selected managed database in your subscription;
- one explicit Admin caller CIDR—empty and allow-all lists are rejected.

The supplied OpenTelemetry Collector Contrib image is pinned by digest. Treat the sidecar as
trusted runtime code: Container Apps identities are app-scoped, so it shares each app's identity
boundary while exporting that replica's traces and Prometheus metrics.

## Prepare matched images

The deployment command does not build, import, tag, or select images. You may import one published
release into your ACR with Azure CLI:

```powershell
$registry = '<registry-name>'
$release = '<release-version-without-v>'

foreach ($service in @('admin', 'ingestion', 'worker')) {
  az acr import `
    --name $registry `
    --source "ghcr.io/arminzou/integrios/$service`:$release" `
    --image "integrios/$service`:$release"
}
```

The Git release tag carries a `v` prefix, while its container tags do not. For example, Git release
`v0.4.1` publishes container tag `0.4.1`.

Resolve the imported manifests to destination ACR digests:

```powershell
foreach ($service in @('admin', 'ingestion', 'worker')) {
  $digest = az acr manifest show-metadata `
    --registry $registry `
    --name "integrios/$service`:$release" `
    --query digest `
    --output tsv
  "$registry.azurecr.io/integrios/$service@$digest"
}
```

Copy `main.example.bicepparam`, replace its placeholder registry, resource group, image digests,
region, names, CIDRs, and secret-reference metadata, and keep the file free of secret values. The
example selects Azure SQL. Set `databaseProvider = 'postgres'` to provision PostgreSQL instead.

`ingestionExternal` and Service Bus coordinates are independent. Leave both Service Bus values
empty for no broker dependency, or supply both an existing namespace name and its resource group to
grant Ingestion `Azure Service Bus Data Receiver`. Configure individual Queue Sources later through
Admin; the namespace role alone does not create or select a broker entity.

## Deploy or update

Use the supplied command for both initial deployment and updates. It prompts without echoing when
secure values are omitted:

```powershell
Copy-Item ./main.example.bicepparam ./main.bicepparam
# Edit ./main.bicepparam before continuing.

./deploy.ps1 `
  -ResourceGroup 'rg-integrios-reference' `
  -Location 'canadacentral' `
  -ParametersFile ./main.bicepparam
```

Automation may pass `SecureString` values through `-DatabaseAdministratorPassword`,
`-OperatorKeySecret`, `-SourceSecret`, and `-DestinationSecret`. The command rejects secret values in
the `.bicepparam` file. For each ARM deployment it creates a randomly named temporary parameter file
containing the four plaintext values because Azure CLI requires materialized deployment parameters,
opens it without file sharing, and deletes it immediately in `finally`. A forced process or machine
termination can prevent that cleanup; inspect the current user's temporary directory before retrying
after an interruption.

The command always:

1. validates nonsecret inputs and immutable images locally;
2. creates or resolves the resource group;
3. reconciles infrastructure with all runtime replicas at zero;
4. runs the selected provider's migrations;
5. runs idempotent Bootstrap and destination-secret validation;
6. reconciles the same images at one replica and waits for healthy active revisions.

A failed migration, Bootstrap, or validation job leaves runtime stopped. After a schema migration,
recover by rolling forward or restoring the database rather than starting an older image set.

Rotate a source or destination value by supplying the replacement secure value and changing
`mappingRevision`; the Key Vault version reference then creates fresh Container Apps revisions.
The logical `SourceSecrets` and `DestinationSecrets` references do not change.

## Author and canary the deployment

Bootstrap creates the deployment-wide OperatorKey only. Use Admin to apply a Connector manifest,
then create a Tenant, TenantApiKey, source and destination Connections, Topic, and Subscription.
The request sequence and public API shapes are in [the setup walkthrough](../../docs/setup.md); use
the deployed Admin and Ingestion HTTPS origins and a real controlled destination instead of its
local Compose addresses.

For an Event API canary, submit one uniquely identified Event with the TenantApiKey and confirm its
Event and Delivery reach successful state. For a Queue Source, configure the existing namespace and
entity through Admin, send one uniquely identified broker message, and confirm the same lifecycle.
Exercise failure and replay only against a destination you control: return a retryable failure until
the Delivery dead-letters, restore the destination, and replay that Delivery through Admin. Integrios
is at-least-once at the downstream HTTP boundary, so an ambiguous downstream response may produce a
repeated request.

## Observability

Each runtime replica sends OTLP traces to its loopback Collector sidecar. The sidecar scrapes the
private operational `/metrics` endpoint, adds Container App and replica labels, and exports through
the Data Collection Rule to Azure Managed Prometheus. JSON stdout flows through native Container
Apps collection to Log Analytics. The `Integrios Operations` Workbook shows outcomes, backlog and
staleness, bounded Connector-class failures and dead letters, and exact Admin `trace_id` lookup.
Observability failures do not participate in liveness or readiness.

## Troubleshooting

- Inspect `main` under the resource group's deployments for an ARM failure.
- Inspect migration, Bootstrap, and validation job executions before restarting runtime.
- Inspect the active Container App revision and its application and Collector logs when readiness
  does not become healthy.
- Confirm the selected database accepts Azure-service traffic and the credentials in Key Vault are
  current.
- Confirm each image digest exists in the configured ACR and each user-assigned identity has
  `AcrPull`.
- For Queue Sources, confirm both Service Bus coordinates were supplied, Ingestion has receiver
  access, and the Admin-authored Source names the intended existing entity.

`/health` is dependency-free liveness. `/ready` checks only the selected database. Service Bus,
individual Sources, destinations, Key Vault after startup, and observability backends deliberately
do not change either probe.

## Customize your copy

Existing-database attachment, strict private access, custom domains, gateways, WAF, autoscaling,
HA, alternate sizing, and different retention or backup settings are Operator-owned adaptations.
Edit the copied Bicep directly rather than expecting this reference to model every Azure topology.
