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
delivery, so no secret values need to be added to its tracked documentation files.

`make up` runs a `bootstrap` one-shot (the `Integrios.Admin` image invoked with plain `bootstrap`)
after migrations and before the services start. It creates only the first OperatorKey credential
used below (bootstrap output, not migration-seeded data) and is idempotent, so re-running `make up`
against an existing EF-managed database is safe. A fresh deployment contains zero Connectors until
the Operator applies a manifest. The dev credential
`global_operator_key:operator_bootstrap_secret` comes from `INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET` in `.env`.

The EF Core cutover does not upgrade databases created by the former Flyway migration path. Delete
the old local database volume before starting this version; this permanently removes its data:

```bash
docker compose down --volumes
make up
```

| Service  | URL                     | Purpose                       |
|----------|-------------------------|-------------------------------|
| Ingestion  | `http://localhost:5231` | Webhook/event intake (data plane) |
| Admin    | `http://localhost:5150` | Tenant and config management (control plane) |
| WireMock | `http://localhost:5054` | Controllable delivery target for testing |

The Worker runs in the background with no HTTP port.

## Quickstart: your first delivered event

This drives the Admin API to onboard a Tenant and a Subscription, sends an Event to Ingestion, and
watches the Worker deliver it to bundled WireMock.

```bash
ADMIN=http://localhost:5150
INGESTION=http://localhost:5231
AUTH="Authorization: OperatorKey global_operator_key:operator_bootstrap_secret"

# 1. Apply the generic HTTP example and capture this deployment's generated Connector ID.
HTTP_CONNECTOR=$(curl -s -X PUT "$ADMIN/admin/connectors/http/versions/1" -H "$AUTH" \
  -H 'Content-Type: application/json' --data-binary @examples/connectors/http.json | jq -r .id)

# 2. Create a tenant
TENANT=$(curl -s -X POST $ADMIN/admin/tenants -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"slug":"acme","name":"Acme","environment":"production"}' | jq -r .id)

# 3. Create an Integrios API key for this generic source (the token is shown once, capture it)
TOKEN=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/tenant-api-keys -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"name":"acme-ingestion"}' | jq -r .token)

# 4. Create a Tenant-owned Destination from the reusable Connector.
# The Destination selects open (unauthenticated) delivery, which the applied HTTP example allows.
DST=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/destinations -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"connector_id\":\"$HTTP_CONNECTOR\",\"name\":\"acme-erp\",\"configuration\":{\"base_uri\":\"http://mocksink:8080/sink/acme-erp\"},\"authentication\":null,\"environment\":\"production\"}" | jq -r .id)

# 5. Create a topic
TOPIC=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/topics -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"name":"payments"}' | jq -r .id)

# 6. Create an Event API Source directly from the Connector and Topic. Event API uses the fixed
# Integrios Event JSON contract and does not configure verification, mapping, or identity extraction.
SOURCE=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/sources -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"connector_id\":\"$HTTP_CONNECTOR\",\"topic_id\":\"$TOPIC\",\"type\":\"event_api\",\"configuration\":{},\"verification\":null,\"input_requirements\":null,\"mapping\":null,\"event_identity_rule\":null}" | jq -r .id)

# 7. Subscribe the destination to payment.created events
curl -s -X POST $ADMIN/admin/tenants/$TENANT/topics/$TOPIC/subscriptions -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"name\":\"acme-erp-sub\",\"match_rules\":{\"event_type\":\"payment.created\"},\"destination_id\":\"$DST\",\"mapping\":null,\"http_delivery\":null,\"http_success\":null,\"order_index\":0}" > /dev/null

# 8. Send an event to the data plane. source_id (query parameter) names the Source; the body is the
# fixed Event API contract -- event_type and payload are required,
# source_event_id is optional; Ingestion combines it with the Source id for idempotency.
EVENT=$(curl -s -X POST "$INGESTION/events?source_id=$SOURCE" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"event_type":"payment.created","source_event_id":"demo-001","payload":{"paymentId":"pay_001","amount":1200}}' | jq -r .event_id)

# 9. Check it was accepted and fanned out to the subscription
curl -s $INGESTION/events/$EVENT -H "Authorization: Bearer $TOKEN" | jq

# 10. See it delivered in WireMock's request journal
curl -s -X POST http://localhost:5054/__admin/requests/find \
  -H 'Content-Type: application/json' \
  -d '{"method":"POST","urlPath":"/sink/acme-erp"}' | jq
```

Destination updates replace the complete `configuration` object rather than merging fields. A
Destination's configuration schema is declared by its Connector's manifest; the example `http`
Connector requires an absolute HTTP(S) `base_uri` with no query or fragment for any Destination an
active Subscription references (see [architecture.md](architecture.md) for the full Connector,
Source, and Destination model, including
Operator-authored Connectors such as the ones in the [GitHub-to-Slack
walkthrough](github-to-slack-walkthrough.md)).

A Topic's `name` is its immutable, Tenant-scoped stream identifier; changing it requires creating a
new Topic. Topic updates may change only the `description`. A Source binds one Connector to one
Topic and carries its own `configuration`; a Webhook Source also carries a generated `callback_id`.
Update a Source to change its mutable configuration, input requirements, or mapping, not the Topic.

The last command should show the delivery request, including its body and headers.

> Inside Compose, services reach WireMock at `http://mocksink:8080` (used in the Destination
> configuration above); from your host it's `http://localhost:5054`.

### Exploring failure handling

The retry policy is deployment-wide. By default, the Worker makes three attempts with exponential
backoff from 30 seconds, so this walkthrough takes about 90 seconds to reach `dead_lettered`.

First, clear earlier receipts, configure the destination to fail, and send a new Event:

```bash
CONTROL_ID=11111111-1111-1111-1111-111111111111
curl -s -X DELETE http://localhost:5054/__admin/requests > /dev/null
curl -s -X POST http://localhost:5054/__admin/mappings -H 'Content-Type: application/json' \
  -d "{\"id\":\"$CONTROL_ID\",\"priority\":1,\"request\":{\"method\":\"POST\",\"urlPath\":\"/sink/acme-erp\"},\"response\":{\"status\":500}}"

FAIL_EVENT=$(curl -s -X POST "$INGESTION/events?source_id=$SOURCE" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"event_type\":\"payment.created\",\"source_event_id\":\"demo-failure-$(date +%s)\",\"payload\":{\"paymentId\":\"pay_failure\",\"amount\":1200}}" \
  | jq -r .event_id)
```

Wait until all three attempts have failed. The third failed attempt exhausts the retry budget and
dead-letters this EventDelivery:

```bash
until curl -fsS $INGESTION/events/$FAIL_EVENT -H "Authorization: Bearer $TOKEN" \
  | jq -e '[.delivery_attempts[] | select(.status == "failed")] | length >= 3' > /dev/null; do
  sleep 5
done

curl -s $INGESTION/events/$FAIL_EVENT -H "Authorization: Bearer $TOKEN" \
  | jq '.delivery_attempts'
```

Reset the sink, discard the failed-request receipts, and replay the dead-lettered EventDelivery.
Recovery is an Operator action through Admin, so it requires the OperatorKey and targets one delivery:

```bash
curl -s -X DELETE http://localhost:5054/__admin/mappings/$CONTROL_ID
curl -s -X DELETE http://localhost:5054/__admin/requests > /dev/null
FAIL_DELIVERY=$(curl -s $ADMIN/admin/tenants/$TENANT/events/$FAIL_EVENT/deliveries -H "$AUTH" \
  | jq -r '.event_deliveries[] | select(.status == "dead_lettered") | .event_delivery_id')
curl -i -s -X POST $ADMIN/admin/tenants/$TENANT/events/$FAIL_EVENT/deliveries/$FAIL_DELIVERY/replay -H "$AUTH"

until curl -fsS $INGESTION/events/$FAIL_EVENT -H "Authorization: Bearer $TOKEN" \
  | jq -e 'any(.delivery_attempts[]; .attempt_number >= 4 and .status == "succeeded")' > /dev/null; do
  sleep 2
done

curl -s -X POST http://localhost:5054/__admin/requests/find \
  -H 'Content-Type: application/json' \
  -d '{"method":"POST","urlPath":"/sink/acme-erp"}' | jq
```

The final receipt proves that replay created a new successful attempt without discarding the three
failed attempts in the Event's history. The `.http` request collections under each service in
`src/` cover the same APIs interactively.

## Rotate the OperatorKey

Every OperatorKey has deployment-wide control-plane authority. Supply the replacement secret out of
band when running the one-shot rotation command:

```bash
docker compose run --rm \
  -e INTEGRIOS_OPERATOR_KEY_ROTATION_SECRET='<replacement-secret>' \
  admin operator-key rotate
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
| `INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET`  | `operator_bootstrap_secret` | `bootstrap` service, Makefile bootstrap targets | Secret for the OperatorKey credential |
| `DOTNET_ENVIRONMENT`                | `Development`            | Makefile bootstrap targets  | Selects `appsettings.Development.json` |
| `INTEGRIOS_DESTINATION_SECRETS_PROVIDER` | `file` | Worker | Selects `file` or `configuration` destination-authentication secret resolution |
| `INTEGRIOS_DESTINATION_SECRETS_DIR` | `./secrets/destination` | Worker | Host directory mounted read-only for destination-authentication values |
| `INTEGRIOS_SOURCE_SECRETS_PROVIDER` | `file` | Ingestion | Selects `file` or `configuration` source-verification secret resolution |
| `INTEGRIOS_SOURCE_SECRETS_DIR` | `./secrets/source` | Ingestion | Host directory mounted read-only for source-verification values |

## Destination-authentication secrets

Destinations store logical secret references, never resolved values. The Worker resolves each
reference immediately before each delivery attempt. This means retries and replay use the current
value after rotation.

The default `file` backend reads one exact UTF-8 value from:

```text
./secrets/destination/<tenant-slug>/<reference>
```

For example, reference `erp_api_key` for tenant `acme` is mounted into the Worker as
`/run/secrets/integrios/destination/acme/erp_api_key`. Values are not trimmed, and the header-based auth
schemes reject values containing CR or LF, so an accidental trailing newline (for example from
`echo` without `-n`) fails delivery. Files may be symlinks, which makes atomic provider-driven
rotation practical. Values must be non-empty, contain no NUL, and be at most 64 KiB.

When the Worker runs directly instead of through Compose, its default file root is
`/run/secrets/integrios/destination` on Linux and
`%ProgramData%\Integrios\secrets\destination` on Windows. Set
`Integrios:DestinationSecrets:FileRoot` to an existing absolute directory to override that native
default. The directory must exist when Worker starts.

The alternative `configuration` backend is selected with
`Integrios:DestinationSecrets:Provider=configuration` (or
`INTEGRIOS_DESTINATION_SECRETS_PROVIDER=configuration` in Compose). It reads
`DestinationSecrets:<tenant-slug>:<reference>` from the Worker's normal .NET configuration.
For local Development, the Worker enables .NET User Secrets:

```bash
dotnet user-secrets --project src/Integrios.Worker set "DestinationSecrets:acme:erp_api_key" "secret-value"
Integrios__DestinationSecrets__Provider=configuration dotnet run --project src/Integrios.Worker
```

Any .NET configuration provider can supply the same key (for example appsettings, environment
variables using `DestinationSecrets__acme__erp_api_key`, or a provider added in your own build). Only the
selected backend is consulted; there is no file/configuration fallback.

Validate resolution without making deliveries:

```bash
docker compose run --rm worker secrets validate --all
docker compose run --rm worker secrets validate --tenant acme
docker compose run --rm worker secrets validate --tenant acme --destination <destination-id>
```

Validation prints references and resolution status, never values. Tenant slugs are lowercase DNS
labels up to 63 characters. References are flat lowercase names up to 63 characters using letters,
digits, and underscores, and must begin with a letter or digit.

Ingestion has a separate source-verification secret capability for webhook Sources. Its file
backend uses `/run/secrets/integrios/source/<tenant-slug>/<reference>` and its configuration backend
reads `SourceSecrets:<tenant-slug>:<reference>`. When Ingestion runs directly, the default Windows
root is `%ProgramData%\Integrios\secrets\source`; the selected directory must exist at startup.
Ingestion cannot
resolve the Worker's destination-authentication namespace, and Admin resolves neither namespace.

Because Admin authors a Source but holds no resolver, only Ingestion can answer whether a Source's
references resolve. Ask it before traffic depends on them:

```bash
docker compose run --rm ingestion secrets validate --all
docker compose run --rm ingestion secrets validate --tenant acme
docker compose run --rm ingestion secrets validate --tenant acme --source <source-id>
```

This covers both shapes a Source carries: a webhook's verification references and a queue Source's
`secret_ref`. It reports references and resolution status, never values, and consumes nothing from a
queue while it runs. Exit codes match the Worker command: `0` all resolvable, `1` one or more
unresolvable, `2` a usage or selection error.

## Useful commands

```bash
make up      # build and start all services (detached)
make down    # stop and remove containers
make logs    # tail all service logs
```

## Migrations

Migrations run automatically during `make up` via the `migrate` service. To run the same pinned
EF Core migration command manually through the Compose network (starting its Postgres dependency if needed):

```bash
make db-migrate
make db-info
```

The local stack intentionally defaults to PostgreSQL. See [Database backends](database-backends.md)
for SQL Server configuration and migration details.

## Production deployment

This guide covers the local dev stack only. For a production reference deployment, see
[`deploy/README.md`](../deploy/README.md).
