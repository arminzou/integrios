# Azure Container Apps reference

This directory is a minimal copy-and-own reference for running one matched Integrios release on
Azure Container Apps. Copy it, review it, and adapt the Bicep to your networking, capacity,
availability, security, and operational requirements.

The reference deploys separate Admin, Ingestion, and Worker Container Apps; migration, Bootstrap,
and secret-validation jobs; one selected managed database; a Key Vault for deployment settings and
one Tenant-secret Key Vault per direction; Log Analytics;
Application Insights; Azure Managed Prometheus; an Operator Workbook; and an Azure Files share for
Admin's Data Protection key ring. It reuses an existing Azure Container Registry and never
provisions Service Bus topology.

## Supplied defaults

| Area | Supplied reference |
|---|---|
| Runtime | One Admin, Ingestion, and Worker replica; no autoscaling or zone redundancy |
| Admin | External HTTPS restricted to explicit Operator CIDRs; optional Microsoft Entra ID dashboard sign-in; durable Data Protection keys shared through storage-encrypted Azure Files |
| Ingestion | External HTTPS by default; may be internal independently of Service Bus access |
| Network | Public Container Apps environment, Key Vault, telemetry endpoints, and database firewall rules; no VNet or private endpoints. The Data Protection share is reachable from public networks with its storage account key, because Container Apps mounts it with that key and only a VNet could restrict it; treat the key as a credential that can forge Operator sessions |
| Azure SQL | General Purpose serverless, one vCore maximum, 0.5 minimum capacity, 60-minute auto-pause, local backup redundancy |
| PostgreSQL | PostgreSQL 16 Burstable B1ms, 32 GiB storage, seven-day local backup, no HA or geo-redundant backup |
| Deployment | Maintenance window: runtime at zero, migrate, Bootstrap, validate secrets, then start one replica |
| Images | Existing Operator-owned ACR; one published release imported into it and deployed pinned by digest |
| Service Bus | Optional receiver role on an existing namespace; no namespace, queue, topic, or subscription creation |

## Prerequisites

- PowerShell 7 and Azure CLI with Bicep;
- an authenticated Azure subscription with the required resource providers registered;
- permission to create resources and role assignments in the deployment, ACR, and optional
  Service Bus resource groups, including importing images into the ACR;
- an existing ACR that can reach `ghcr.io`, or one already holding the matched Admin, Ingestion,
  and Worker images;
- a region available to both Container Apps and the selected managed database in your subscription;
- one explicit Admin caller CIDR—empty and allow-all lists are rejected;
- permission to assign yourself `Key Vault Secrets Officer` on the two Tenant-secret vaults after
  the first deployment (see [Tenant secrets](#tenant-secrets)).

The supplied OpenTelemetry Collector Contrib image is pinned by digest. Treat the sidecar as
trusted runtime code: Container Apps identities are app-scoped, so it shares each app's identity
boundary while exporting that replica's traces and Prometheus metrics.

Every app and job runs as exactly one user-assigned identity, selected through `AZURE_CLIENT_ID`.

## Select a release

Set `release` in the parameter file to a published version, without the Git tag's `v` prefix: Git
release `v0.9.0` publishes container tag `0.9.0`. On each run, `deploy.ps1` imports any of that
release's Admin, Ingestion, and Worker images missing from your registry from
`ghcr.io/arminzou/integrios`, resolves each to its digest, and deploys those digests. Upgrading is
a change to `release` followed by the same command.

A tag the registry already holds is used as it is and never re-imported, so the first import of a
release fixes the digests every later deployment of it resolves to.

To deploy images you built or imported yourself, remove `release` and set `adminImage`,
`ingestionImage`, and `workerImage` to full `<registry>.azurecr.io/<repository>@sha256:<digest>`
references from one matched build. The command accepts one form or the other, never both.

Copy `main.example.bicepparam`, replace its placeholder registry, resource group, region, names,
and CIDRs, and keep the file free of secret values. The
example selects Azure SQL. Set `databaseProvider = 'postgres'` to provision PostgreSQL instead.

`ingestionExternal` and Service Bus coordinates are independent. Leave both Service Bus values
empty for no broker dependency, or supply both an existing namespace name and its resource group to
grant Ingestion `Azure Service Bus Data Receiver`. Configure individual broker Sources later through
Admin; the namespace role alone does not create or select a broker entity.

## Deploy or update

Use the supplied command for both initial deployment and updates. It needs no secret input
unless dashboard sign-in is enabled, when it prompts for the OpenID Connect client secret without
echoing it:

```powershell
Copy-Item ./main.example.bicepparam ./main.bicepparam
# Edit ./main.bicepparam before continuing.

./deploy.ps1 `
  -ResourceGroup 'rg-integrios-reference' `
  -Location 'canadacentral' `
  -ParametersFile ./main.bicepparam
```

### What the command does

1. Checks what Azure cannot: no secret values in the parameter file, a release or digest-pinned
   images from the configured registry, a matching location, no allow-all Admin CIDR, and paired
   Service Bus and sign-in settings.
2. For a `release`, imports any missing images and resolves each to its digest.
3. Reads the stored initial OperatorKey secret, or generates it on the first deployment.
4. Reconciles infrastructure with all runtime replicas at zero.
5. Runs migrations as the Migrate identity, the schema owner and database Entra administrator.
6. Grants control-plane scope to Admin and Bootstrap, and data-plane scope to Ingestion, Worker,
   and secret validation.
7. Runs idempotent Bootstrap and destination-secret validation.
8. Starts one replica of each app and waits for healthy revisions.

A failed migration, grant, Bootstrap, or validation job leaves runtime stopped. After a schema
migration, recover by rolling forward or restoring the database rather than starting an older image
set. Validation checks every Destination secret reference, so store a new Destination's secret
before the next deployment.

### Deployment secrets

Database access uses managed identities, so there is no database password. The first deployment
generates the initial OperatorKey secret and stores it in the deployment-settings vault as
`operator-key-bootstrap`; later runs reuse it. The command grants you read access to that vault and
prints its name. Read the secret with:

```powershell
az keyvault secret show --vault-name <deployment-settings-vault> --name operator-key-bootstrap `
  --query value --output tsv
```

It only seeds the first key: after `operator-key rotate` it no longer authenticates. Automation may
pass `-OperatorKeySecret` and `-AdminOidcClientSecret` as `SecureString` values; secret values in the
`.bicepparam` file are rejected. The command writes them to a temporary parameter file for Azure CLI
and deletes it; after a forced interruption, check your temporary directory.

## Tenant secrets

Tenant secrets are not template inputs. Ingestion and Worker read them at startup from their own
vault through the standard .NET Key Vault configuration source, so any number of Tenants' secrets
can be added without editing or redeploying the template:

| Vault (deployment output `secretVaults`) | Readable by | Secret name |
|---|---|---|
| `source` | Ingestion | `SourceSecrets--<tenant-slug>--<secret-reference>` |
| `destination` | Worker and the secret-validation job | `DestinationSecrets--<tenant-slug>--<secret-reference>` |

Each identity holds `Key Vault Secrets User` on its own vault only, so neither runtime process can
read the other direction's secrets. The separate deployment-settings vault holds the bootstrap
OperatorKey and optional dashboard OIDC secret. Keep anything else out of the Tenant-secret vaults:
every secret in a vault becomes a configuration key of the process that reads it.

The Key Vault configuration source maps `--` to the configuration separator, so
`DestinationSecrets--acme--erp-api-key` resolves as `DestinationSecrets:acme:erp-api-key`. Key
Vault secret names are at most 127 characters, so keep each Tenant slug and secret reference pair
short enough to fit behind the prefix.

Once, after the first deployment, grant yourself write access to both vaults:

```powershell
$resourceGroup = 'rg-integrios-reference'
$vaults = az deployment group show --resource-group $resourceGroup --name main `
  --query properties.outputs.secretVaults.value | ConvertFrom-Json
$me = az ad signed-in-user show --query id --output tsv
foreach ($vault in @($vaults.source, $vaults.destination)) {
  az role assignment create --assignee-object-id $me --assignee-principal-type User `
    --role 'Key Vault Secrets Officer' `
    --scope (az keyvault show --name $vault --query id --output tsv)
}
```

To add or rotate a secret, set it in the owning vault, then restart the owning app so it reloads
the vault. For a Destination secret:

```powershell
az keyvault secret set --vault-name $vaults.destination `
  --name 'DestinationSecrets--acme--erp-api-key' --file ./erp-api-key.txt --encoding utf-8
$app = az deployment group show --resource-group $resourceGroup --name main `
  --query properties.outputs.appNames.value.worker --output tsv
$revision = az containerapp revision list --resource-group $resourceGroup --name $app `
  --query '[?properties.active].name | [0]' --output tsv
az containerapp revision restart --resource-group $resourceGroup --name $app --revision $revision
```

Use `$vaults.source`, `SourceSecrets--<tenant-slug>--<secret-reference>`, and the `ingestion` app
for a Source secret. Reading the value from a file keeps it out of shell history; leading and
trailing line breaks are trimmed at resolution. To check Destination references without a
redeployment, start the job named by the `jobNames.validateSecrets` output with
`az containerapp job start`. A vault that the
app's identity cannot reach stops the app from starting, and a newly created role assignment can
take a few minutes to apply.

## Dashboard sign-in with Microsoft Entra ID

The reference deploys Admin without dashboard sign-in until you configure it; the dashboard loads
and OperatorKey automation works either way. Admin's address exists only after the first
deployment, so enabling sign-in takes two runs:

1. Deploy with `adminOidcAuthority` and `adminOidcClientId` empty. The command prints the OpenID
   Connect redirect and sign-out redirect URIs; the `adminOidcRedirectUris` deployment output holds
   the same values.
2. In the Microsoft Entra admin center, register a single-tenant application. Add both printed URIs
   as **Web** redirect URIs, then create a client secret and note when it expires.
3. Under **Enterprise applications**, open the application, set **Assignment required** to **Yes**,
   and assign only the people who should operate Integrios. Admin creates an Operator for every
   identity the provider lets through, so this assignment is the admission boundary: without it,
   anyone in the tenant can sign in with full Operator authority.
4. Set `adminOidcAuthority` to `https://login.microsoftonline.com/<tenant-id>/v2.0` and
   `adminOidcClientId` to the application (client) ID, then run `deploy.ps1` again. It prompts for
   the client secret and stores it in Key Vault, where only Admin's identity can read it.

Sign-in is a browser redirect, so the Operator's own address must be inside `adminAllowedCidrs`;
Entra never calls Admin directly. Container Apps ingress terminates TLS, so the reference sets
`ASPNETCORE_FORWARDEDHEADERS_ENABLED` on Admin to keep the callback on `https`.

To rotate the client secret, create a new one in Entra and run `deploy.ps1` with it before the old
one expires; the new Key Vault version starts a fresh Admin revision. Removing an assignment
blocks the next sign-in, but a session already issued stays valid until sign-out or its fixed
eight-hour lifetime ends. [The dashboard guide](../../docs/operator-dashboard.md) covers enabling
password sign-in as a fallback, which this reference does not configure.

## Author and canary the deployment

Bootstrap creates the deployment-wide OperatorKey only. Use Admin to apply a Connector manifest,
then create a Tenant, TenantApiKey, source and destination Connections, Topic, and Subscription.
The request sequence and public API shapes are in [the setup walkthrough](../../docs/setup.md); use
the deployed Admin and Ingestion HTTPS origins and a real controlled destination instead of its
local Compose addresses.

For an Event API canary, submit one uniquely identified Event with the TenantApiKey and confirm its
Event and Delivery reach successful state. For a broker Source, configure the existing namespace and
entity through Admin, send one uniquely identified broker message, and confirm the same lifecycle.
Exercise failure and replay only against a destination you control: return a retryable failure until
the Delivery dead-letters, restore the destination, and replay that Delivery through Admin. Integrios
is at-least-once at the downstream HTTP boundary, so an ambiguous downstream response may produce a
repeated request.

## Observability

Each runtime replica sends OTLP traces to its loopback Collector sidecar. The sidecar scrapes the
private operational `/metrics` endpoint, adds Container App and replica labels, and exports through
the Data Collection Rule to Azure Managed Prometheus, authenticating as the app's user-assigned
identity, which holds `Monitoring Metrics Publisher` on that rule. JSON stdout flows through native Container
Apps collection to Log Analytics. The `Integrios Operations` Workbook shows outcomes, backlog and
staleness, bounded Connector-class failures and dead letters, and exact Admin `trace_id` lookup.
Observability failures do not participate in liveness or readiness.

## Troubleshooting

- Inspect `main` under the resource group's deployments for an ARM failure.
- Inspect migration, grant, Bootstrap, and validation job executions before restarting runtime.
- Inspect the active Container App revision and its application and Collector logs when readiness
  does not become healthy.
- Confirm the selected database accepts Azure-service traffic and the Migrate identity remains its
  Entra administrator and schema owner.
- Confirm each resolved image digest exists in the configured ACR and each user-assigned identity
  has `AcrPull`. The command prints the digests it resolved, and the `main` deployment records them
  as the `adminImage`, `ingestionImage`, and `workerImage` parameters.
- If Ingestion, Worker, or the validation job fails at startup with a Key Vault error, confirm its
  identity holds `Key Vault Secrets User` on its own Tenant-secret vault; a new assignment can take
  a few minutes to apply.
- If telemetry stops arriving, check the Collector logs for an authentication error and confirm
  the app's user-assigned identity holds `Monitoring Metrics Publisher` on the Data Collection
  Rule.
- For broker Sources, confirm both Service Bus coordinates were supplied, Ingestion's
  user-assigned identity has receiver access, and the Admin-authored Source names the intended
  existing entity.

`/health` is dependency-free liveness. `/ready` checks only the selected database. Service Bus,
individual Sources, destinations, Key Vault after startup, and observability backends deliberately
do not change either probe.

## Emergency database access

No person holds standing database access. In an emergency, an Azure administrator of the resource
group grants it to themself temporarily from a workstation and removes it afterward. Objects created
during the session are owned by that person; hand any new object to the Migrate identity before
leaving.

### PostgreSQL

PostgreSQL allows several Entra administrators, so add yourself beside the Migrate identity:

```powershell
$me = az ad signed-in-user show --output json | ConvertFrom-Json
az postgres flexible-server firewall-rule create --resource-group <resource-group> --server-name <postgres-server> --name break-glass --start-ip-address <your-ip> --end-ip-address <your-ip>
az postgres flexible-server microsoft-entra-admin create --resource-group <resource-group> --server-name <postgres-server> --display-name $me.userPrincipalName --object-id $me.id --type User
$env:PGPASSWORD = az account get-access-token --resource-type oss-rdbms --query accessToken --output tsv
psql "host=<postgres-server>.postgres.database.azure.com dbname=integrios user=$($me.userPrincipalName) sslmode=require"
```

After the repair:

```powershell
az postgres flexible-server microsoft-entra-admin delete --resource-group <resource-group> --server-name <postgres-server> --object-id $me.id --yes
az postgres flexible-server firewall-rule delete --resource-group <resource-group> --server-name <postgres-server> --name break-glass --yes
```

### Azure SQL

Azure SQL has one Entra administrator. Add a temporary firewall rule for your address, replace the
Migrate identity with yourself using `az sql server ad-admin create`, and connect with Entra
authentication. Do not run `deploy.ps1` while you hold the role. Afterward, restore the Migrate
identity with the same command and remove the firewall rule; the next `deploy.ps1` run also restores
the administrator.

## Customize your copy

### Completed-history retention

The reference leaves completed-history retention disabled. To opt in, add
`Integrios__Worker__HistoryRetention__Period` to the Worker's environment in `main.bicep` with a
.NET `TimeSpan` of at least seven days. Read the destructive first-sweep, backup, and migration
write-blocking guidance in [the production deployment guide](../README.md#completed-history-retention)
before enabling it. Removing the setting stops future deletion but does not restore deleted history.

### Other adaptations

Existing-database attachment, strict private access, custom domains, gateways, WAF, autoscaling,
HA, alternate sizing, and different retention or backup settings are Operator-owned adaptations.
Edit the copied Bicep directly rather than expecting this reference to model every Azure topology.
