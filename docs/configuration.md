# Runtime configuration reference

Admin, Ingestion, and Worker use standard .NET configuration. `appsettings.json` supplies
non-secret defaults where present; `appsettings.Development.json` adds local-only values.
Environment variables override those files. For a hierarchical key, replace each `:` with `__`:
`Database:Provider` becomes `Database__Provider`. The tables below show effective defaults even
when a host also states them in `appsettings.json`.

Ingestion and Worker additionally load key-per-file and, when configured, Azure Key Vault after
the standard sources. A value from either later source overrides an earlier value for the same
key. They load at startup; restart the owning process after changing a Tenant secret. See
[Tenant secrets](../secrets/README.md) for file names and process isolation. Compose variables
such as `INTEGRIOS_ADMIN_OIDC_AUTHORITY` are deployment-file inputs that Compose maps to the
runtime keys below; see [the deployment example](../deploy/.env.example).

## Shared and host settings

| Configuration key | Environment variable | Reader | Default or required value | Valid values and effect |
|---|---|---|---|---|
| `Database:Provider` | `Database__Provider` | All three | `postgres` | `postgres` or `sqlserver` (case-insensitive). Selects the matching connection string. |
| `OperationalPort` | `OperationalPort` | Admin, Ingestion | `5299` | TCP port 1–65535 for `/health`, `/ready`, and `/metrics`. |
| `WorkerMetricsPort` | `WorkerMetricsPort` | Worker | `5299` | TCP port 1–65535 for Worker's operational endpoints. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | same | All three | Unset: no trace export | If set, an absolute HTTP(S) URI. Metrics remain available on the operational port. |
| `Integrios:PublicIngestionBaseUri` | `Integrios__PublicIngestionBaseUri` | Admin | Required; Development file uses `http://localhost:5231` | Absolute HTTP(S) origin, optionally with a path prefix; no user info, query, or fragment. HTTPS required outside Development. Used to show webhook callback URLs. |
| `Integrios:Admin:TraceUrlTemplate` | `Integrios__Admin__TraceUrlTemplate` | Admin | Unset: show a copyable trace ID only | Absolute HTTP(S) URI, no user info, with `{trace_id}` exactly once. |
| `Integrios:Admin:DataProtection:KeyRingPath` | `Integrios__Admin__DataProtection__KeyRingPath` | Admin | Required; Development file uses `../../artifacts/data-protection` | Writable directory shared by Admin replicas to preserve browser sessions. |
| `Integrios:BrokerSources:ReconcileSeconds` | `Integrios__BrokerSources__ReconcileSeconds` | Ingestion | `30` | Integer seconds between broker Source reconciliations. Use a positive value; the current reader does not enforce a range. |
| `Integrios:Telemetry:OutboxDepthSampleInterval` | `Integrios__Telemetry__OutboxDepthSampleInterval` | Worker | `00:00:15` | Positive `TimeSpan` for backlog metric sampling. |

These standard framework and OpenTelemetry settings are present in the supplied deployments;
their detailed validation belongs to .NET or OpenTelemetry rather than Integrios:

| Setting / environment variable | Reader | Default or requirement |
|---|---|---|
| `DOTNET_ENVIRONMENT` | All three | .NET defaults to `Production`; the dev Compose stack sets `Development` and loads `appsettings.Development.json`. |
| `ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, `ASPNETCORE_HTTPS_PORTS` | Admin, Ingestion | Product listener; without one, the host uses `http://localhost:5000`. The operational listener uses its separate port. Worker serves only its operational listener. |
| `Logging:LogLevel:*` / `Logging__LogLevel__*` | All three | Host-specific levels in `appsettings.json`; override individual categories through configuration. |
| `AllowedHosts` | Admin, Ingestion | `*` in their `appsettings.json` files; restrict for an exposed deployment. |
| `OTEL_TRACES_SAMPLER`, `OTEL_TRACES_SAMPLER_ARG` | All three | Standard trace sampling settings; see [observability](observability.md#traces). |
| `OTEL_RESOURCE_ATTRIBUTES` | All three | Unset; adds deployment-owned trace and metric resource attributes. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | Admin behind a proxy | Set to `true` when forwarded HTTPS scheme is needed for dashboard sign-in. |

## Admin dashboard and sign-in

OpenID Connect is enabled when `Authority` is non-empty. Password sign-in is independent of OIDC.
With both disabled, Admin still accepts OperatorKey automation.

| Configuration key | Environment variable | Default or required value | Valid values and effect |
|---|---|---|---|
| `Integrios:Admin:Password:Enabled` | `Integrios__Admin__Password__Enabled` | `false` | Boolean; shows the managed-password sign-in path when enabled. |
| `Integrios:Admin:Session:Lifetime` | `Integrios__Admin__Session__Lifetime` | `08:00:00` | Positive `TimeSpan`; non-sliding browser session lifetime. |
| `Integrios:Admin:Oidc:Authority` | `Integrios__Admin__Oidc__Authority` | Unset: OIDC disabled | Non-empty issuer when OIDC is enabled. |
| `Integrios:Admin:Oidc:ClientId` | `Integrios__Admin__Oidc__ClientId` | Required when OIDC is enabled | Non-blank client ID. |
| `Integrios:Admin:Oidc:ClientSecret` | `Integrios__Admin__Oidc__ClientSecret` | Unset | Operational credential supplied outside checked-in settings when the provider needs it. |
| `Integrios:Admin:Oidc:DisplayName` | `Integrios__Admin__Oidc__DisplayName` | `OpenID Connect` | Sign-in label. |
| `Integrios:Admin:Oidc:CallbackPath` | `Integrios__Admin__Oidc__CallbackPath` | `/auth/callback` | Callback path registered with the provider. |
| `Integrios:Admin:Oidc:SignedOutCallbackPath` | `Integrios__Admin__Oidc__SignedOutCallbackPath` | `/auth/signed-out` | Sign-out callback path. |
| `Integrios:Admin:Oidc:RequireHttpsMetadata` | `Integrios__Admin__Oidc__RequireHttpsMetadata` | `true`; Development file uses `false` | Boolean. Keep `true` for an HTTPS provider; also controls secure browser cookies. |
| `Integrios:Admin:Oidc:Scopes` | `Integrios__Admin__Oidc__Scopes` | `openid profile email` | Space-separated scopes. |

See [Operator dashboard access](operator-dashboard.md) for provider setup and durable Data
Protection storage.

## Worker processing

All durations below use .NET `TimeSpan` syntax, such as `00:00:30`. Invalid values fail Worker
configuration; positive durations and the relationships in the last column are checked before
processing starts.

| Configuration key | Environment variable | Default | Constraint |
|---|---|---|---|
| `Integrios:Delivery:HttpTimeout` | `Integrios__Delivery__HttpTimeout` | `00:00:30` | Positive; less than `AttemptDeadline`. |
| `Integrios:Delivery:AttemptDeadline` | `Integrios__Delivery__AttemptDeadline` | `00:00:45` | Greater than `HttpTimeout`; less than `LeaseDuration` and `ShutdownGracePeriod`. |
| `Integrios:Delivery:LeaseDuration` | `Integrios__Delivery__LeaseDuration` | `00:02:00` | Greater than `AttemptDeadline`. |
| `Integrios:Delivery:ShutdownGracePeriod` | `Integrios__Delivery__ShutdownGracePeriod` | `00:01:00` | Greater than `AttemptDeadline`. |
| `Integrios:Delivery:Retry:BaseDelay` | `Integrios__Delivery__Retry__BaseDelay` | `00:00:30` | Positive. |
| `Integrios:Delivery:Retry:MaxAttempts` | `Integrios__Delivery__Retry__MaxAttempts` | `3` | Integer at least 1. |
| `Integrios:Worker:FanoutLoop:BatchSize` | `Integrios__Worker__FanoutLoop__BatchSize` | `10` | Positive integer. |
| `Integrios:Worker:FanoutLoop:IdlePollInterval` | `Integrios__Worker__FanoutLoop__IdlePollInterval` | `00:00:02` | Positive `TimeSpan`. |
| `Integrios:Worker:DeliveryLoop:BatchSize` | `Integrios__Worker__DeliveryLoop__BatchSize` | `25` | Positive integer. |
| `Integrios:Worker:DeliveryLoop:IdlePollInterval` | `Integrios__Worker__DeliveryLoop__IdlePollInterval` | `00:00:02` | Positive `TimeSpan`. |
| `Integrios:Worker:HistoryRetention:Period` | `Integrios__Worker__HistoryRetention__Period` | Unset: disabled | `TimeSpan` of at least seven days. Enabling deletion requires reviewing [retention behavior](../deploy/README.md#completed-history-retention). |

## Credentials and Tenant secret sources

Credential values belong in environment variables or deployment-owned secret stores, not in
checked-in production `appsettings.json`. The Development files contain disposable local database
defaults only. Do not put deployment credentials in `secrets/sources` or
`secrets/destinations`; those directories are for Tenant secret values.

| Configuration key | Environment variable | Reader | Requirement |
|---|---|---|---|
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | All three and Admin setup commands | Non-blank when `Database:Provider=postgres`. |
| `ConnectionStrings:SqlServer` | `ConnectionStrings__SqlServer` | All three and Admin setup commands | Non-blank when `Database:Provider=sqlserver`. |
| `Database:Postgres:Authentication` | `Database__Postgres__Authentication` | All three and Admin setup commands | `Password` (default) or `AzureEntra`; AzureEntra requires a username and a password-free connection string. |
| `Database:RuntimePrincipals:<index>:Name` | `Database__RuntimePrincipals__<index>__Name` | Admin `database grant-runtime` | Required principal name. |
| `Database:RuntimePrincipals:<index>:Scope` | `Database__RuntimePrincipals__<index>__Scope` | Admin `database grant-runtime` | `control-plane` or `data-plane`. |
| `Database:RuntimePrincipals:<index>:Password` | `Database__RuntimePrincipals__<index>__Password` | Admin `database grant-runtime` | Optional; sets or replaces the password on every run. Omit credential keys for grant-only mode. |
| `Database:RuntimePrincipals:<index>:EntraClientId` | `Database__RuntimePrincipals__<index>__EntraClientId` | Admin `database grant-runtime` | Optional Azure SQL Entra client ID. |
| `Database:RuntimePrincipals:<index>:EntraObjectId` | `Database__RuntimePrincipals__<index>__EntraObjectId` | Admin `database grant-runtime` | Optional Azure PostgreSQL Entra object ID. |
| `INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET` | same | Admin `bootstrap` command | Non-empty in Production for the initial OperatorKey; supplied out of band. |
| `INTEGRIOS_OPERATOR_KEY_ROTATION_SECRET` | same | Admin `operator-key rotate` command | Non-empty for a rotation. |
| `Integrios:KeyVault:Uri` | `Integrios__KeyVault__Uri` | Ingestion, Worker | Optional. If set, the owning process loads its direction-specific vault at startup; an invalid or unreachable vault stops startup. |
| `SourceSecrets:<tenant-slug>:<secret-reference>` | `SourceSecrets__<tenant-slug>__<secret-reference>` | Ingestion | Open-ended Tenant secret namespace; see [file and vault naming](../secrets/README.md). |
| `DestinationSecrets:<tenant-slug>:<secret-reference>` | `DestinationSecrets__<tenant-slug>__<secret-reference>` | Worker | Open-ended Tenant secret namespace; see [file and vault naming](../secrets/README.md). |

`database grant-runtime` requires a non-empty list with unique principal names and a `control-plane`
or `data-plane` scope on every entry. Credential keys may be omitted for grant-only mode; if a key
is present but empty, including an unset Compose variable, validation fails before SQL runs. A
supplied Password is applied on every run. Entra IDs must be GUIDs, and the selected provider
requires its matching ID. Do not pass secret values as command-line configuration.

For Compose, `INTEGRIOS_SOURCE_SECRETS_DIR` defaults to `./secrets/sources` and
`INTEGRIOS_DESTINATION_SECRETS_DIR` defaults to `./secrets/destinations` relative to the Compose
file. Each is mounted only into its owning process at `/run/secrets/integrios`.
