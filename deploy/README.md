# Production reference deployment

This is the PostgreSQL reference production Compose deployment for Integrios. It is copy-and-own:
copy this directory into your own infrastructure repo and adapt it to your environment. For an
Azure Container Apps reference instead, see [deploy/azure](azure/README.md).

The root `compose.yml` at the repository root is the local development stack. It builds images
from source and bundles a test sink and dashboards; it is not for deployment.

## Quick start

```bash
cp .env.example .env
# edit .env: set POSTGRES_PASSWORD, INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET, and INTEGRIOS_PUBLIC_INGESTION_BASE_URI
# The image version needs no edit: compose.yml defaults to the release this checkout ships.
mkdir -p secrets
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
capabilities the Admin API already exposes. It is off unless the deployment configures an identity
provider: with `INTEGRIOS_ADMIN_OIDC_AUTHORITY` empty, Admin serves no browser surface and no
sign-in path, and stays machine-only on OperatorKey exactly as before.

To turn it on, register a confidential OpenID Connect client for this deployment with
`<admin origin>/auth/callback` as its redirect URI, then set `INTEGRIOS_ADMIN_OIDC_AUTHORITY`,
`INTEGRIOS_ADMIN_OIDC_CLIENT_ID`, and `INTEGRIOS_ADMIN_OIDC_CLIENT_SECRET` in `.env`. Anyone the
provider issues a token for becomes an Operator with deployment-wide authority on first sign-in, so
restrict who the provider will issue for.

Serve Admin over HTTPS when the dashboard is on. The session cookie is secure-only, so a browser
will not store it over plain HTTP and sign-in cannot complete.

The session cookie is protected with keys this container generates. A single Admin container is
therefore the supported shape today: running several replicas behind a load balancer needs shared,
durable Data Protection key storage, which this reference does not yet configure.

OperatorKey automation is unaffected. It continues to authenticate the same way whether or not the
dashboard is enabled.

## Directional Connection secrets

The Worker defaults to the `file` backend. For every destination-authentication secret reference,
it reads the current value from the host directory configured by
`INTEGRIOS_DESTINATION_SECRETS_DIR`, mounted read-only at:

```text
/run/secrets/integrios/destination/<tenant-slug>/<reference>
```

Create one file per logical reference. File contents are exact UTF-8 and are not trimmed; values
must be non-empty, contain no NUL, and be at most 64 KiB. The header-based auth schemes reject
values containing CR or LF, so an accidental trailing newline (for example from `echo` without
`-n`) fails delivery. Symlinks are supported. Each delivery
attempt performs a fresh read, so rotate a file or symlink atomically and subsequent retries and
replays see the new value.

Set `INTEGRIOS_DESTINATION_SECRETS_PROVIDER=configuration` to use the Worker's .NET configuration
instead. That backend reads `DestinationSecrets:<tenant-slug>:<reference>`. In your owned Compose file, supply those
keys through the .NET provider you choose—for example an added configuration package/source or
environment keys such as `DestinationSecrets__acme__erp_api_key`. The Worker consults exactly one selected
backend and never falls back between configuration and files.

Check the configured references before traffic or after rotation:

```bash
docker compose run --rm worker secrets validate --all
docker compose run --rm worker secrets validate --tenant acme
docker compose run --rm worker secrets validate --tenant acme --connection <connection-id>
```

The command exits `0` when all selected references resolve, `1` when any do not, and `2` for an
invalid selection or startup configuration. It prints no resolved values.

Ingestion source-verification values use the separate `INTEGRIOS_SOURCE_SECRETS_DIR` mount at
`/run/secrets/integrios/source`. With the configuration backend, Ingestion reads
`SourceSecrets:<tenant-slug>:<reference>`. Admin resolves no secret values, Ingestion has
no access to destination-authentication values, and Worker has no access to source-verification
values.

## Upgrading

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
