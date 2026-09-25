# Setup

Run the full Integrios stack locally with Docker Compose and walk an event from intake to
delivery.

## Prerequisites

- Docker (with the Compose plugin) and `make`
- `curl` and `jq` for the quickstart below

## Start the stack

```bash
make up   # builds images, starts Postgres, runs migrations, grants runtime scopes, bootstrap, then services
```

No configuration is needed: the dev stack carries working defaults. To override any of them,
create a `.env` file (see the environment variables table below).

The checkout includes the `secrets/` mount directory used by the default file-based secret
provider. Secret files placed there are ignored by Git; the quickstart uses unauthenticated
delivery, so no secret values need to be added to its tracked documentation files.

`make up` runs `database migrate`, then `database grant-runtime`, then a `bootstrap` one-shot (the
`Integrios.Admin` image invoked with plain `bootstrap`) before the services start. Migration uses
the database owner; Admin and Bootstrap use the control-plane principal; Ingestion and Worker use
the data-plane principal. It creates only the first OperatorKey credential
used below (bootstrap output, not migration-seeded data) and is idempotent, so re-running `make up`
against an existing EF-managed database is safe. A fresh deployment contains zero Connectors until
the Operator applies a manifest. The dev credential
`global_operator_key:operator_bootstrap_secret` comes from `INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET` in `.env`.

To use the browser dashboard, configure either supported human sign-in method and create any needed
Password credential by following [Operator dashboard access](operator-dashboard.md). The dashboard
is intentionally absent while both OIDC and password sign-in are disabled.

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
  -d '{"key":"payments","name":"Payments"}' | jq -r .id)

# 6. Create an Event API Source directly from the Connector and Topic. Event API uses the fixed
# Integrios Event JSON contract and does not configure verification, mapping, or identity extraction.
SOURCE=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/sources -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"connector_id\":\"$HTTP_CONNECTOR\",\"topic_id\":\"$TOPIC\",\"name\":\"Payments Event API\",\"type\":\"event_api\",\"event_types\":[\"payment.created\"],\"configuration\":{},\"verification\":null,\"input_requirements\":null,\"mapping\":null,\"event_identity_rule\":null}" | jq -r .id)

# 7. Subscribe the destination to payment.created events
SUB=$(curl -s -X POST $ADMIN/admin/tenants/$TENANT/topics/$TOPIC/subscriptions -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"name\":\"acme-erp-sub\",\"event_types\":[\"payment.created\"],\"destination_id\":\"$DST\",\"mapping\":null,\"http_delivery\":null,\"http_success\":null,\"order_index\":0}" | jq -r .id)

# Sources and Subscriptions are created Inactive, so nothing flows until each is activated.
curl -s -X POST $ADMIN/admin/tenants/$TENANT/sources/$SOURCE/activate -H "$AUTH" > /dev/null
curl -s -X POST $ADMIN/admin/tenants/$TENANT/topics/$TOPIC/subscriptions/$SUB/activate -H "$AUTH" > /dev/null

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
Connector requires an absolute HTTP(S) `base_uri` with no query or fragment for any Destination a
Subscription references (see [architecture.md](architecture.md) for the full Connector,
Source, and Destination model, including
Operator-authored Connectors such as the ones in the [GitHub-to-Slack
walkthrough](github-to-slack-walkthrough.md)).

A Topic's `key` is its immutable, Tenant-scoped stream identifier; its `name` is a mutable label.
A Source binds one Connector to one Topic, has its own mutable `name`, and carries its own
`configuration`; a Webhook Source also carries a generated `callback_id`. Update a Source to change
its label, mutable configuration, input requirements, or mapping, not the Topic binding.

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
a working local value. Create a `.env` at the repo root only to override. For runtime keys,
defaults, and valid values across all three hosts, see the
[configuration reference](configuration.md).

| Variable                            | Default                  | Used by                     | Purpose                         |
|-------------------------------------|--------------------------|-----------------------------|---------------------------------|
| `POSTGRES_USER`                     | `integrios`              | compose, Makefile `db-*`    | Database username               |
| `POSTGRES_PASSWORD`                 | `integrios_dev`          | compose, Makefile `db-*`    | Database password               |
| `INTEGRIOS_ADMIN_RUNTIME_PASSWORD` | `admin_runtime_dev` | `grant-runtime`, Admin, Bootstrap | Local control-plane database password |
| `INTEGRIOS_DATA_RUNTIME_PASSWORD` | `data_runtime_dev` | `grant-runtime`, Ingestion, Worker | Local data-plane database password |
| `INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET`  | `operator_bootstrap_secret` | `bootstrap` service, Makefile bootstrap targets | Secret for the OperatorKey credential |
| `DOTNET_ENVIRONMENT`                | `Development`            | Makefile bootstrap targets  | Selects `appsettings.Development.json` |
| `INTEGRIOS_ADMIN_OIDC_AUTHORITY` | empty | Admin | OIDC issuer; setting it enables OIDC dashboard sign-in |
| `INTEGRIOS_ADMIN_OIDC_DISPLAY_NAME` | `OpenID Connect` | Admin | Provider label shown on the sign-in gate |
| `INTEGRIOS_ADMIN_PASSWORD_ENABLED` | `false` | Admin | Enables Integrios-managed password sign-in |
| `INTEGRIOS_DESTINATION_SECRETS_DIR` | `./secrets/destinations` | Worker | Host key-per-file directory mounted read-only at `/run/secrets/integrios` |
| `INTEGRIOS_SOURCE_SECRETS_DIR` | `./secrets/sources` | Ingestion | Host key-per-file directory mounted read-only at `/run/secrets/integrios` |

## Tenant secrets

Sources and Destinations store secret references, never values. Each process resolves the values
it needs from its own .NET configuration, under one key per Tenant and reference:

| Process | Configuration key | Used for |
|---|---|---|
| Ingestion | `SourceSecrets:<tenant-slug>:<secret-reference>` | Webhook verification and broker Source credentials |
| Worker | `DestinationSecrets:<tenant-slug>:<secret-reference>` | Destination authentication |

Ingestion never reads `DestinationSecrets`, Worker never reads `SourceSecrets`, and Admin resolves
neither. A secret reference is a lowercase DNS label, the same grammar as a Tenant slug: letters,
digits, and single hyphens, up to 63 characters, starting and ending with a letter or digit (for
example `erp-api-key`). Consecutive hyphens are rejected because Azure Key Vault reads `--` as a
key separator. Manifest field names such as `required_secret_refs` and the keys inside
`secret_refs` stay snake_case; only the reference values follow this grammar.

### Where values come from

Ingestion and Worker read the standard .NET configuration sources, then two more that are added
after them and therefore win for the same key:

1. appsettings files, User Secrets in Development, environment variables, and command-line
   arguments, in the framework's usual order. An environment variable uses `__` as the separator:
   `DestinationSecrets__acme__erp-api-key`.
2. Key-per-file from `/run/secrets/integrios`, when that directory exists. Each file name is a
   configuration key with `__` as the separator, and its content is the value. Files whose names
   start with `ignore.` are skipped.
3. Azure Key Vault, when `Integrios:KeyVault:Uri` is set. The process authenticates with
   `DefaultAzureCredential`, and a secret named `DestinationSecrets--acme--erp-api-key` becomes the
   key `DestinationSecrets:acme:erp-api-key`. An unreachable or unauthorized vault stops the
   process from starting.

Every source loads once at startup. **Adding or rotating a value takes effect when the process
restarts**; retries and replays after the restart use the new value.

In the dev stack, `secrets/sources` holds only Tenant Source secrets and is mounted into Ingestion;
`secrets/destinations` holds only Tenant Destination secrets and is mounted into Worker. Deployment
credentials such as database connection strings come from environment variables. Each directory
is mounted read-only at `/run/secrets/integrios`:

```bash
printf %s 'secret-value' > secrets/destinations/DestinationSecrets__acme__erp-api-key
docker compose restart worker
```

Every file in a mounted directory becomes a configuration key, so mount only that process's own
directory into it. The key-per-file root is fixed. If Ingestion and Worker run directly on one host
outside containers, they share that root and would both load every value in it, so supply their
secrets through per-process environment variables, User Secrets, or a vault instead. For local
Development without containers, both hosts enable .NET User Secrets:

```bash
dotnet user-secrets --project src/Integrios.Worker set "DestinationSecrets:acme:erp-api-key" "secret-value"
dotnet run --project src/Integrios.Worker
```

### Supplying the files on other platforms

Anything that can place one file per key in `/run/secrets/integrios` works:

- **Kubernetes Secret**: mount a Secret whose keys are the file names, for example
  `DestinationSecrets__acme__erp-api-key`, as a volume at `/run/secrets/integrios` on the Worker
  only. Secret keys allow `_` and `-`.
- **Secrets Store CSI driver**: mount the driver's volume at the same path and set each object's
  alias (`objectAlias` or the provider's equivalent) to the key-per-file name.
- **Docker secrets** (Compose or Swarm): set each secret's `target` to
  `/run/secrets/integrios/<key-per-file name>`.

Kubernetes and the CSI driver update mounted files in place, but Integrios reads them only at
startup, so roll the Deployment after a change.

### Value rules

Values are UTF-8, non-empty, contain no NUL, and are at most 64 KiB. Key-per-file strips one
trailing line break, and resolution trims leading and trailing CR and LF, so a file written with
`echo` resolves to the same value as one written with `printf %s`. Invalid UTF-8 fails resolution.
The header-based auth schemes reject values that still contain CR or LF.

The `oauth2_client_credentials` scheme stores `token_endpoint`, `client_id`, an explicit
`client_secret_basic` or `client_secret_post` method, optional `scope`, and a `client_secret`
reference. The Worker obtains and reuses bearer tokens in process memory until their early-refresh
boundary. It never persists the client secret, access token, or token response; each Worker replica
maintains its own cache and reacquires after restart.

### Validate before traffic depends on it

Each process answers for its own references and prints references and resolution status, never
values:

```bash
docker compose run --rm worker secrets validate --all
docker compose run --rm worker secrets validate --tenant acme
docker compose run --rm worker secrets validate --tenant acme --destination <destination-id>

docker compose run --rm ingestion secrets validate --all
docker compose run --rm ingestion secrets validate --tenant acme
docker compose run --rm ingestion secrets validate --tenant acme --source <source-id>
```

The Ingestion command covers a webhook's verification references and a broker Source's
`secret_ref`, and consumes nothing from a broker while it runs. Both commands start a new process,
so they see values added since the running services started. Exit codes: `0` all resolvable, `1`
one or more unresolvable, `2` a usage, selection, or startup error such as an unreachable vault.

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
