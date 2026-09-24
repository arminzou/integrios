# Production reference deployment

This is the PostgreSQL reference production Compose deployment for Integrios. It is copy-and-own:
copy this directory into your own infrastructure repo and adapt it to your environment. For an
Azure Container Apps reference instead, see [deploy/azure](azure/README.md).

The root `compose.yml` at the repository root is the local development stack. It builds images
from source and bundles a test sink and dashboards; it is not for deployment. See the
[configuration reference](../docs/configuration.md) for runtime keys, defaults, and valid values.

## Quick start

```bash
cp .env.example .env
# edit .env: set POSTGRES_PASSWORD, INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET, and INTEGRIOS_PUBLIC_INGESTION_BASE_URI
# The image version needs no edit: compose.yml defaults to the release this checkout ships.
mkdir -p secrets/sources secrets/destinations
docker compose up -d
```

Startup order is enforced by `depends_on`: `postgres` becomes healthy, then `migrate` runs the
EF Core migrations to completion, then `bootstrap` runs its one-shot, then `ingestion`, `admin`,
and `worker` start.

## Bootstrap semantics

The `bootstrap` service is idempotent and safe to re-run. It creates no Connectors and, only if no
live deployment-wide OperatorKey exists yet, creates the first OperatorKey.

The OperatorKey secret comes from `INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET`. Production bootstrap requires a
non-empty Operator-supplied value and never prints the secret. The OperatorKey credential format is:

```text
global_operator_key:<secret>
```

Every OperatorKey has deployment-wide Operator authority. Rotate it by supplying the replacement
secret out of band to the one-shot Admin CLI:

```bash
docker compose run --rm \
  -e INTEGRIOS_OPERATOR_KEY_ROTATION_SECRET='<replacement-secret>' \
  admin operator-key rotate
```

Rotation atomically revokes the previous live key and creates its replacement. The command prints
only the replacement public identifier; it never generates or outputs the replacement secret.

## Operator dashboard

The Admin service serves a browser dashboard over the same origin as its API, for the same
capabilities the Admin API already exposes. Configure OpenID Connect, Integrios-managed email and
password, or both. With neither enabled, Admin stays machine-only on OperatorKey and serves no
browser surface. See [Operator dashboard access](../docs/operator-dashboard.md) for provider
settings, interactive credential provisioning, and recovery.

Serve Admin over HTTPS when the dashboard is on. The session cookie is secure-only, so a browser
will not store it over plain HTTP and sign-in cannot complete.

The session cookie, antiforgery token, and pagination cursors use the Data Protection key ring in
the `admin_data_protection` named volume. Preserve that volume across Admin replacement and mount
the same storage into every Admin replica. The key files are sensitive mutable state: restrict
volume access and use storage-level encryption.

OperatorKey automation is unaffected. It continues to authenticate the same way whether or not the
dashboard is enabled.

## Tenant secrets

Ingestion and Worker resolve Tenant secrets from standard .NET configuration: Ingestion reads
`SourceSecrets:<tenant-slug>:<secret-reference>` and Worker reads
`DestinationSecrets:<tenant-slug>:<secret-reference>`. Admin resolves neither, and neither runtime
process reads the other's namespace.

This deployment feeds them through key-per-file configuration. The `secrets/` directories hold
Tenant secret values only; deployment credentials such as database connection strings come from
environment variables. Each process mounts its own flat directory read-only at
`/run/secrets/integrios`, where each file name is a configuration key with `__` as the separator:

| Host directory | Variable | Mounted into | File name |
|---|---|---|---|
| `./secrets/sources` | `INTEGRIOS_SOURCE_SECRETS_DIR` | Ingestion | `SourceSecrets__<tenant-slug>__<secret-reference>` |
| `./secrets/destinations` | `INTEGRIOS_DESTINATION_SECRETS_DIR` | Worker | `DestinationSecrets__<tenant-slug>__<secret-reference>` |

```bash
printf %s 'secret-value' > secrets/destinations/DestinationSecrets__acme__erp-api-key
docker compose restart worker
```

Values load once at startup: **restart the owning service after adding or rotating a value**.
Every file in a mounted directory becomes a configuration key (except names starting with
`ignore.`), so never mount one process's directory into the other. Key-per-file values override
environment variables and command-line values for the same key. Environment variables such as
`DestinationSecrets__acme__erp-api-key`, or Azure Key Vault through `Integrios__KeyVault__Uri`,
supply the same keys without files. See [Tenant secrets](../docs/setup.md#tenant-secrets) for
reference grammar, value rules, and mappings for Kubernetes Secrets, CSI drivers, and Docker
secrets.

Check the configured references before traffic depends on them and after every change:

```bash
docker compose run --rm worker secrets validate --all
docker compose run --rm worker secrets validate --tenant acme
docker compose run --rm worker secrets validate --tenant acme --destination <destination-id>
docker compose run --rm ingestion secrets validate --all
```

Each command exits `0` when all selected references resolve, `1` when any do not, and `2` for an
invalid selection or startup configuration. It prints no resolved values.

## Upgrading

### Tenant secrets move to standard .NET configuration

This release is a clean break with no compatibility path for the previous secret layout:

- The `file`/`configuration` provider switch is gone, along with
  `INTEGRIOS_SOURCE_SECRETS_PROVIDER`, `INTEGRIOS_DESTINATION_SECRETS_PROVIDER`, and every
  `*Secrets:Provider` and `*Secrets:FileRoot` setting.
- The per-Tenant file layout (`<tenant-slug>/<reference>` below a per-direction root) is no longer
  read. Use flat key-per-file names in the owning process's directory, as in
  [Tenant secrets](#tenant-secrets). The `INTEGRIOS_SOURCE_SECRETS_DIR` and
  `INTEGRIOS_DESTINATION_SECRETS_DIR` variables override the default host directories.
- Secret references are now kebab-case: lowercase letters, digits, and single hyphens.
  Underscored references are rejected when a Source or Destination is created or updated, and an
  existing one no longer resolves. Change each reference in Admin (for example `erp_api_key` to
  `erp-api-key`) and rename its value to match.
- Key-per-file skips files whose names start with `ignore.`. Key-per-file and Key Vault load after
  environment variables and override them when they supply the same key.
- Values load at startup, so a rotated value takes effect when the process restarts, not on the
  next delivery attempt.

After upgrading, run `secrets validate --all` for both Worker and Ingestion (see above) and fix
every unresolved reference before sending traffic.

The first EF Core-managed release does not upgrade a database created by the former migration
system. Export anything you need, then provision an empty database before starting that release.
This is a destructive schema cutover; subsequent EF-managed releases migrate normally.

Worker scheduling is configured independently for the two durable queues:

| Setting | Default |
| --- | --- |
| `Integrios:Worker:FanoutLoop:BatchSize` | `10` |
| `Integrios:Worker:FanoutLoop:IdlePollInterval` | `00:00:02` |
| `Integrios:Worker:DeliveryLoop:BatchSize` | `25` |
| `Integrios:Worker:DeliveryLoop:IdlePollInterval` | `00:00:02` |

If you previously customized
`Integrios:Delivery:IdlePollInterval`, replace it with
`Integrios:Worker:FanoutLoop:IdlePollInterval` and/or
`Integrios:Worker:DeliveryLoop:IdlePollInterval`. The old key is no longer read. The new fanout and
delivery loop defaults are both two seconds, so deployments that used the old default need no change.

### Completed-history retention

Completed-history retention is disabled unless the Worker receives
`Integrios:Worker:HistoryRetention:Period`. In Compose, set
`INTEGRIOS_HISTORY_RETENTION_PERIOD` and uncomment the matching Worker environment mapping. The
value is a .NET `TimeSpan` of at least seven days, such as `30.00:00:00` for 30 days. The Worker
runs one bounded sweep at startup and then hourly; its cadence and 500-Event batch size are fixed.

Enabling retention is destructive. The first sweep may permanently delete every eligible terminal
Event aggregate older than the cutoff, including its processed outbox row, EventDeliveries, and
DeliveryAttempts. That also ends its replay, diagnostics, and deduplication horizon. Size and back
up the database before opting in. Removing the setting stops future sweeps but cannot restore
history already deleted.

The enabling release also creates a filtered index over processed outbox rows. PostgreSQL and SQL
Server build that index with their normal migration operation rather than an online/concurrent
variant, so a large existing outbox can experience write blocking while migration runs. Schedule
the upgrade in an appropriate maintenance window and monitor the migration before starting the
matched runtime images.

Pull the `deploy/` directory for the release you are moving to — its `compose.yml` already
defaults to that version — or set `INTEGRIOS_VERSION` in `.env` to pin one explicitly. All three
services resolve from that single value, so they always upgrade as a matched set. Never point it
at a mutable tag such as `latest`, which tracks unreleased commits on the default branch. Then:

```bash
docker compose pull
docker compose up -d
```

Migrations run automatically via the `migrate` one-shot on every `up`.

## Using a managed Postgres

Remove the `postgres` service from `compose.yml`, then point `ConnectionStrings__Postgres` in
`migrate`, `bootstrap`, `ingestion`, `admin`, and `worker` at your database.

## Using SQL Server 2022+

Use an externally managed SQL Server 2022 or later, remove the bundled `postgres` service, and adjust the
`migrate` dependency. On `migrate`, `bootstrap`, `ingestion`, `admin`, and `worker`, replace the
PostgreSQL connection setting with:

```yaml
environment:
  Database__Provider: sqlserver
  ConnectionStrings__SqlServer: ${INTEGRIOS_SQLSERVER_CONNECTION_STRING}
```

Keep the same startup order and matched image version. The migration one-shot selects the SQL
Server migration assembly automatically. Both `READ_COMMITTED_SNAPSHOT` settings are supported;
see [Database backends](../docs/database-backends.md) for the queue-locking policy.

## Ports

| Service | Port | Purpose |
|---------|------|---------|
| ingestion | 5231 | Webhook/event intake (data plane) |
| ingestion | 5232 (loopback only) | Operational: `/health`, `/ready`, metrics |
| admin   | 5150 | Tenant and config management (control plane) |
| admin   | 5151 (loopback only) | Operational: `/health`, `/ready`, metrics |
| worker  | 5299 (loopback only) | Operational: `/health`, `/ready`, metrics |

The worker exposes no product HTTP port — only its operational listener. Every operational port is
bound to `127.0.0.1`, not the container's external interface; override the host-side port with
`INTEGRIOS_INGESTION_OPERATIONAL_PORT`, `INTEGRIOS_ADMIN_OPERATIONAL_PORT`, or
`INTEGRIOS_WORKER_METRICS_PORT` in `.env`. See [Observability](../docs/observability.md) for what
each operational endpoint returns.
