# Setup

Run the full Integrios stack locally with Docker Compose and walk an event from intake to
delivery.

## Prerequisites

- Docker (with the Compose plugin) and `make`
- `curl` and `jq` for the quickstart below

## Start the stack

```bash
make up   # builds images, starts Postgres, runs migrations, bootstrap, then the services
```

No configuration is needed: the dev stack carries working defaults. To override any of them,
create a `.env` file (see the environment variables table below).

The checkout includes the `secrets/` mount directory used by the default file-based secret
provider. Secret files placed there are ignored by Git; the quickstart uses unauthenticated
connections, so no secret values need to be added to its tracked documentation files.

`make up` runs a `bootstrap` one-shot (the `Integrios.Admin` image invoked with plain `bootstrap`)
after migrations and before the services start. It creates the built-in `webhook` integration and
the admin credential used below (bootstrap output, not migration-seeded data), and is
idempotent, so re-running `make up` against an existing database is safe. The dev credential
`global_admin_key:admin_bootstrap_secret` comes from `INTEGRIOS_BOOTSTRAP_ADMIN_SECRET` in `.env`.

| Service  | URL                     | Purpose                       |
|----------|-------------------------|-------------------------------|
| Ingress  | `http://localhost:5231` | Webhook/event intake (data plane) |
| Admin    | `http://localhost:5150` | Tenant and config management (control plane) |
| MockSink | `http://localhost:5054` | Controllable delivery target for testing |

The Worker runs in the background with no HTTP port.

## Quickstart: your first delivered event

This drives the Admin API to onboard a Tenant and a Subscription, sends an Event to Ingress, and
watches the Worker deliver it to the bundled MockSink.

```bash
ADMIN=http://localhost:5150
INGRESS=http://localhost:5231
AUTH="Authorization: AdminKey global_admin_key:admin_bootstrap_secret"
WEBHOOK=00000000-0000-0000-0000-000000000001   # deployment-wide built-in HTTP Integration

# 1. Create a tenant
TENANT=$(curl -s -X POST $ADMIN/admin/tenants -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"slug":"acme","name":"Acme","environment":"production"}' | jq -r .id)

# 2. Create an Integrios API key for this generic source (the token is shown once, capture it)
TOKEN=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/api-keys -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"name":"acme-ingress"}' | jq -r .token)

# 3. Create Tenant-owned source and destination Connections from the same reusable Integration
SRC=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/connections -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"integrationId\":\"$WEBHOOK\",\"name\":\"acme-source\",\"config\":{\"url\":\"http://mocksink:8080/sink/acme-source\"},\"environment\":\"production\"}" | jq -r .id)
DST=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/connections -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"integrationId\":\"$WEBHOOK\",\"name\":\"acme-erp\",\"config\":{\"url\":\"http://mocksink:8080/sink/acme-erp\"},\"environment\":\"production\"}" | jq -r .id)

# 4. Create a topic fed by the source connection
TOPIC=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/topics -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"name\":\"payments\",\"sourceConnectionIds\":[\"$SRC\"]}" | jq -r .id)

# 5. Subscribe the destination to payment.created events
curl -s -X POST $ADMIN/admin/tenants/$TENANT/topics/$TOPIC/subscriptions -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"name\":\"acme-erp-sub\",\"matchRules\":{\"event_type\":\"payment.created\"},\"destinationConnectionId\":\"$DST\"}" > /dev/null

# 6. Send an event to the data plane
EVENT=$(curl -s -X POST $INGRESS/events -H "Authorization: ApiKey $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"sourceConnectionId\":\"$SRC\",\"topicName\":\"payments\",\"eventType\":\"payment.created\",\"payload\":{\"paymentId\":\"pay_001\",\"amount\":1200},\"idempotencyKey\":\"demo-001\"}" | jq -r .eventId)

# 7. Check it was accepted and fanned out to the subscription
curl -s $INGRESS/events/$EVENT -H "Authorization: ApiKey $TOKEN" | jq

# 8. See it delivered in the sink
docker compose logs mocksink | grep MockSink
```

Connection updates replace the complete `config` object rather than merging fields. Connections
whose Integration direction is `destination` or `both` must include an absolute HTTP(S)
`config.url` on both create and update. When upgrading a deployment with a legacy
destination-capable Connection that has no URL, include a valid URL the next time that Connection
is updated. Source-only Integration configuration remains free-form.

This quickstart reflects the current implementation: Integrations are built-in and read-only, and
delivery is a JSON `POST` to `config.url`. The finalized product model keeps HTTP as the only
destination protocol while adding Operator-authored reusable Integration definitions and
Subscription-owned method, relative-path, restricted-header, and JSON-or-no-body configuration.

Topic update requests must include the Topic's current `name`. The name is its immutable,
Tenant-scoped stream identifier; changing it requires creating a new Topic. Updates may change the
description and, when supplied, replace `sourceConnectionIds`.

The last command should show a line like:

```
[MockSink] acme-erp received event (mode=succeed): {"amount": 1200, "paymentId": "pay_001"}
```

> Inside Compose, services reach MockSink at `http://mocksink:8080` (used in the connection
> config above); from your host it's `http://localhost:5054`.

### Exploring failure handling

The retry policy is deployment-wide. By default, the Worker makes three attempts with exponential
backoff from 30 seconds, so this walkthrough takes about 90 seconds to reach `dead_lettered`.

First, clear earlier receipts, configure the destination to fail, and send a new Event:

```bash
curl -s -X DELETE http://localhost:5054/receipts/acme-erp > /dev/null
curl -s -X PUT http://localhost:5054/control/acme-erp -H 'Content-Type: application/json' -d '{"mode":"fail"}'

FAIL_EVENT=$(curl -s -X POST $INGRESS/events \
  -H "Authorization: ApiKey $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"sourceConnectionId\":\"$SRC\",\"topicName\":\"payments\",\"eventType\":\"payment.created\",\"payload\":{\"paymentId\":\"pay_failure\",\"amount\":1200},\"idempotencyKey\":\"demo-failure-$(date +%s)\"}" \
  | jq -r .eventId)
```

Wait until all three attempts have failed. The third failed attempt exhausts the retry budget and
dead-letters this SubscriptionDelivery:

```bash
until curl -fsS $INGRESS/events/$FAIL_EVENT -H "Authorization: ApiKey $TOKEN" \
  | jq -e '[.deliveryAttempts[] | select(.status == "failed")] | length >= 3' > /dev/null; do
  sleep 5
done

curl -s $INGRESS/events/$FAIL_EVENT -H "Authorization: ApiKey $TOKEN" \
  | jq '.deliveryAttempts'
```

Reset the sink, discard the failed-request receipts, and replay the same Event. Replay returns
`202 Accepted` only when it finds a dead-lettered delivery to schedule again:

```bash
curl -s -X DELETE http://localhost:5054/control/acme-erp
curl -s -X DELETE http://localhost:5054/receipts/acme-erp > /dev/null
curl -i -s -X POST $INGRESS/events/$FAIL_EVENT/replay -H "Authorization: ApiKey $TOKEN"

until curl -fsS $INGRESS/events/$FAIL_EVENT -H "Authorization: ApiKey $TOKEN" \
  | jq -e 'any(.deliveryAttempts[]; .attemptNumber >= 4 and .status == "succeeded")' > /dev/null; do
  sleep 2
done

curl -s http://localhost:5054/receipts/acme-erp | jq
```

The final receipt proves that replay created a new successful attempt without discarding the three
failed attempts in the Event's history. The `.http` request collections under each service in
`src/` cover the same APIs interactively.

## Rotate the Operator AdminKey

Every AdminKey has deployment-wide control-plane authority. Supply the replacement secret out of
band when running the one-shot rotation command:

```bash
docker compose run --rm \
  -e INTEGRIOS_ADMIN_KEY_ROTATION_SECRET='<replacement-secret>' \
  admin admin-key rotate
```

Rotation atomically revokes the previous live key and creates its replacement. The command prints
the new public identifier but never generates or outputs the replacement secret.

## Environment variables

The dev stack needs no `.env` file: `compose.yml` and the `Makefile` default every variable to
a working local value. Create a `.env` at the repo root only to override.

| Variable                            | Default                  | Used by                     | Purpose                         |
|-------------------------------------|--------------------------|-----------------------------|---------------------------------|
| `POSTGRES_USER`                     | `integrios`              | compose, Makefile `db-*`    | Database username               |
| `POSTGRES_PASSWORD`                 | `integrios_dev`          | compose, Makefile `db-*`    | Database password               |
| `INTEGRIOS_BOOTSTRAP_ADMIN_SECRET`  | `admin_bootstrap_secret` | `bootstrap` service, Makefile bootstrap targets | Secret for the admin credential |
| `DOTNET_ENVIRONMENT`                | `Development`            | Makefile bootstrap targets  | Selects `appsettings.Development.json` |
| `INTEGRIOS_SECRETS_PROVIDER`        | `file`                   | Worker                      | Selects `file` or `configuration` secret resolution |
| `INTEGRIOS_SECRETS_DIR`             | `./secrets`              | Worker                      | Host directory mounted read-only for the file backend |
| `INTEGRIOS_SOURCE_VERIFICATION_SECRETS_PROVIDER` | `file` | Ingress | Selects `file` or `configuration` source-verification resolution |
| `INTEGRIOS_SOURCE_VERIFICATION_SECRETS_DIR` | `./source-verification-secrets` | Ingress | Separate read-only host directory for source-verification values |

## Delivery secrets

Connections store logical secret references, never resolved values. The Worker resolves each
reference immediately before each delivery attempt. This means retries and replay use the current
value after rotation.

The default `file` backend reads one exact UTF-8 value from:

```text
./secrets/<tenant-slug>/<reference>
```

For example, reference `erp_api_key` for tenant `acme` is mounted into the Worker as
`/run/secrets/integrios/acme/erp_api_key`. Values are not trimmed, and the header-based auth
schemes reject values containing CR or LF, so an accidental trailing newline (for example from
`echo` without `-n`) fails delivery. Files may be symlinks, which makes atomic provider-driven
rotation practical. Values must be non-empty, contain no NUL, and be at most 64 KiB.

When the Worker runs directly instead of through Compose, its default file root is
`/run/secrets/integrios` on Linux and `%ProgramData%\Integrios\secrets` on Windows. Set
`Integrios:Secrets:FileRoot` to an existing absolute directory to override that native default.

The alternative `configuration` backend is selected with
`Integrios:Secrets:Provider=configuration` (or `INTEGRIOS_SECRETS_PROVIDER=configuration` in
Compose). It reads `Secrets:<tenant-slug>:<reference>` from the Worker's normal .NET configuration.
For local Development, the Worker enables .NET User Secrets:

```bash
dotnet user-secrets --project src/Integrios.Worker set "Secrets:acme:erp_api_key" "secret-value"
Integrios__Secrets__Provider=configuration dotnet run --project src/Integrios.Worker
```

Any .NET configuration provider can supply the same key (for example appsettings, environment
variables using `Secrets__acme__erp_api_key`, or a provider added in your own build). Only the
selected backend is consulted; there is no file/configuration fallback.

Validate resolution without making deliveries:

```bash
docker compose run --rm worker secrets validate --all
docker compose run --rm worker secrets validate --tenant acme
docker compose run --rm worker secrets validate --tenant acme --connection <connection-id>
```

Validation prints references and resolution status, never values. Tenant slugs are lowercase DNS
labels up to 63 characters. References are flat lowercase names up to 63 characters using letters,
digits, and underscores, and must begin with a letter or digit.

Ingress has a separate source-verification secret capability for built-in source adapters. Its file
backend uses `/run/secrets/integrios-source-verification/<tenant-slug>/<reference>` and its
configuration backend reads `SourceVerificationSecrets:<tenant-slug>:<reference>`. Ingress cannot
resolve the Worker's destination-authentication namespace, and Admin resolves neither namespace.

## Useful commands

```bash
make up      # build and start all services (detached)
make down    # stop and remove containers
make logs    # tail all service logs
```

## Migrations

Migrations run automatically during `make up` via the `migrate` service. To run them manually
against a local Postgres on `localhost:5432`:

```bash
make db-migrate
make db-info
```

## Production deployment

This guide covers the local dev stack only. For a production reference deployment, see
[`deploy/README.md`](../deploy/README.md).
