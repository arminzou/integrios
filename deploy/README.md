# Production reference deployment

This is the reference production Compose deployment for Integrios. It is copy-and-own: copy
this directory, plus `db/`, into your own infrastructure repo and adapt it to your environment.

The root `compose.yml` at the repository root is the local development stack. It builds images
from source and bundles a test sink and dashboards; it is not for deployment.

## Quick start

```bash
cp .env.example .env
# edit .env: set POSTGRES_PASSWORD, INTEGRIOS_VERSION, and optionally INTEGRIOS_BOOTSTRAP_ADMIN_SECRET
docker compose up -d
```

Startup order is enforced by `depends_on`: `postgres` becomes healthy, then `migrate` runs the
Flyway migrations to completion, then `bootstrap` runs its one-shot, then `ingress`, `admin`,
and `worker` start.

## Bootstrap semantics

The `bootstrap` service is idempotent and safe to re-run. It creates the built-in integration
catalog and, only if no live global admin key exists yet, the first admin key.

The admin secret comes from `INTEGRIOS_BOOTSTRAP_ADMIN_SECRET`. If you set it in `.env`, that
secret is used. If you leave it empty, bootstrap generates a random secret and prints it once
to the bootstrap container's logs; retrieve it with:

```bash
docker compose logs bootstrap
```

Store it immediately, it is not shown again. The admin credential format is:

```text
global_admin_key:<secret>
```

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
