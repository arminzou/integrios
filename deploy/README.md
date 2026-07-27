# Production reference deployment

This is the reference production Compose deployment for Integrios. It is copy-and-own: copy
this directory, plus `db/`, into your own infrastructure repo and adapt it to your environment.

The root `compose.yml` at the repository root is the local development stack. It builds images
from source and bundles a test sink and dashboards; it is not for deployment.

## Quick start

```bash
cp .env.example .env
# edit .env: set POSTGRES_PASSWORD, INTEGRIOS_VERSION, and INTEGRIOS_BOOTSTRAP_ADMIN_SECRET
mkdir -p secrets
docker compose up -d
```

Startup order is enforced by `depends_on`: `postgres` becomes healthy, then `migrate` runs the
Flyway migrations to completion, then `bootstrap` runs its one-shot, then `ingress`, `admin`,
and `worker` start.

## Bootstrap semantics

The `bootstrap` service is idempotent and safe to re-run. It creates the built-in integration
catalog and, only if no live deployment-wide AdminKey exists yet, the first AdminKey.

The admin secret comes from `INTEGRIOS_BOOTSTRAP_ADMIN_SECRET`. Production bootstrap requires a
non-empty Operator-supplied value and never prints the secret. The admin credential format is:

```text
global_admin_key:<secret>
```

Every AdminKey has deployment-wide Operator authority. Rotate it by supplying the replacement
secret out of band to the one-shot Admin CLI:

```bash
docker compose run --rm \
  -e INTEGRIOS_ADMIN_KEY_ROTATION_SECRET='<replacement-secret>' \
  admin admin-key rotate
```

Rotation atomically revokes the previous live key and creates its replacement. The command prints
only the replacement public identifier; it never generates or outputs the replacement secret.

## Delivery secrets

The Worker defaults to the `file` backend. For every connection secret reference, it reads the
current value from the host directory configured by `INTEGRIOS_SECRETS_DIR`, mounted read-only at:

```text
/run/secrets/integrios/<tenant-slug>/<reference>
```

Create one file per logical reference. File contents are exact UTF-8 and are not trimmed; values
must be non-empty, contain no NUL, and be at most 64 KiB. The header-based auth schemes reject
values containing CR or LF, so an accidental trailing newline (for example from `echo` without
`-n`) fails delivery. Symlinks are supported. Each delivery
attempt performs a fresh read, so rotate a file or symlink atomically and subsequent retries and
replays see the new value.

Set `INTEGRIOS_SECRETS_PROVIDER=configuration` to use the Worker's .NET configuration instead.
That backend reads `Secrets:<tenant-slug>:<reference>`. In your owned Compose file, supply those
keys through the .NET provider you choose—for example an added configuration package/source or
environment keys such as `Secrets__acme__erp_api_key`. The Worker consults exactly one selected
backend and never falls back between configuration and files.

Check the configured references before traffic or after rotation:

```bash
docker compose run --rm worker secrets validate --all
docker compose run --rm worker secrets validate --tenant acme
docker compose run --rm worker secrets validate --tenant acme --connection <connection-id>
```

The command exits `0` when all selected references resolve, `1` when any do not, and `2` for an
invalid selection or startup configuration. It prints no resolved values.

## Upgrading

Bump `INTEGRIOS_VERSION` in `.env`, then:

```bash
docker compose pull
docker compose up -d
```

Migrations run automatically via the `migrate` one-shot on every `up`.

## Using a managed Postgres

Remove the `postgres` service from `compose.yml`, then point `FLYWAY_URL` (in `migrate`) and
`ConnectionStrings__Postgres` (in `bootstrap`, `ingress`, `admin`, `worker`) at your database.

## Ports

| Service | Port | Purpose |
|---------|------|---------|
| ingress | 5231 | Webhook/event intake (data plane) |
| admin   | 5150 | Tenant and config management (control plane) |

The worker exposes no HTTP port.
