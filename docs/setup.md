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

This drives the Admin API to onboard a tenant and a route, sends an event to Ingress, and
watches the Worker deliver it to the bundled MockSink.

```bash
ADMIN=http://localhost:5150
INGRESS=http://localhost:5231
AUTH="Authorization: AdminKey global_admin_key:admin_bootstrap_secret"
WEBHOOK=00000000-0000-0000-0000-000000000001   # built-in generic webhook integration

# 1. Create a tenant
TENANT=$(curl -s -X POST $ADMIN/admin/tenants -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"slug":"acme","name":"Acme","environment":"production"}' | jq -r .id)

# 2. Create a data-plane API key (the token is shown once, capture it)
TOKEN=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/api-keys -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"name":"acme-ingress","scopes":["events:write"]}' | jq -r .token)

# 3. Create source and destination connections (destination points at MockSink)
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
  -d '{"topicName":"payments","eventType":"payment.created","payload":{"paymentId":"pay_001","amount":1200},"idempotencyKey":"demo-001"}' | jq -r .eventId)

# 7. Check it was accepted and fanned out to the subscription
curl -s $INGRESS/events/$EVENT -H "Authorization: ApiKey $TOKEN" | jq

# 8. See it delivered in the sink
docker compose logs mocksink | grep MockSink
```

The last command should show a line like:

```
[MockSink] acme-erp received event (mode=succeed): {"amount": 1200, "paymentId": "pay_001"}
```

> Inside Compose, services reach MockSink at `http://mocksink:8080` (used in the connection
> config above); from your host it's `http://localhost:5054`.

### Exploring failure handling

Force the sink to fail or slow down and watch the Worker retry, dead-letter, then replay:

```bash
# make the destination sink fail; the Worker retries per the subscription policy
curl -s -X PUT http://localhost:5054/control/acme-erp -H 'Content-Type: application/json' -d '{"mode":"fail"}'
# reset it back to success
curl -s -X DELETE http://localhost:5054/control/acme-erp
# replay an event by id
curl -s -X POST $INGRESS/events/$EVENT/replay -H "Authorization: ApiKey $TOKEN"
```

The `.http` request collections under each service in `src/` cover these flows in full.

## Environment variables

The dev stack needs no `.env` file: `compose.yml` and the `Makefile` default every variable to
a working local value. Create a `.env` at the repo root only to override.

| Variable                            | Default                  | Used by                     | Purpose                         |
|-------------------------------------|--------------------------|-----------------------------|---------------------------------|
| `POSTGRES_USER`                     | `integrios`              | compose, Makefile `db-*`    | Database username               |
| `POSTGRES_PASSWORD`                 | `integrios_dev`          | compose, Makefile `db-*`    | Database password               |
| `INTEGRIOS_BOOTSTRAP_ADMIN_SECRET`  | `admin_bootstrap_secret` | `bootstrap` service, Makefile bootstrap targets | Secret for the admin credential |
| `DOTNET_ENVIRONMENT`                | `Development`            | Makefile bootstrap targets  | Selects `appsettings.Development.json` |

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
