#!/usr/bin/env bash
# Seeds a fresh Tenant wired for HTTP delivery to the run's mocksink through the Admin API:
# http Connector, Tenant, TenantApiKey, Destination, Topic, active Event API Source, active
# Subscription for payment.created. Prints exports; load them with: eval "$(seed-delivery.sh)".
# Requires the run's env (source artifacts/verify/<run>/state/env).
set -euo pipefail

: "${ADMIN:?source the run env first}"
J='Content-Type: application/json'
SLUG=${SEED_SLUG:-v$(date +%s)}
ROOT=$(git rev-parse --show-toplevel)

post() { curl -fsS --max-time 10 -X POST "$ADMIN$1" -H "$AUTH" -H "$J" -d "$2"; }

CONNECTOR=$(curl -fsS --max-time 10 -X PUT "$ADMIN/admin/connectors/http/versions/1" -H "$AUTH" -H "$J" \
  --data-binary "@$ROOT/examples/connectors/http.json" | jq -er .connector.id)
TENANT=$(post /admin/tenants "{\"slug\":\"$SLUG\",\"name\":\"Verify $SLUG\",\"environment\":\"production\"}" | jq -er .id)
TOKEN=$(post "/admin/tenants/$TENANT/tenant-api-keys" '{"name":"verify-ingestion"}' | jq -er .token)
DESTINATION=$(post "/admin/tenants/$TENANT/destinations" \
  "{\"connector_id\":\"$CONNECTOR\",\"name\":\"$SLUG-sink\",\"configuration\":{\"base_uri\":\"$MOCKSINK/sink/$SLUG\"},\"authentication\":null,\"environment\":\"production\"}" | jq -er .id)
TOPIC=$(post "/admin/tenants/$TENANT/topics" '{"key":"payments","name":"Payments"}' | jq -er .id)
SOURCE=$(post "/admin/tenants/$TENANT/sources" \
  "{\"connector_id\":\"$CONNECTOR\",\"topic_id\":\"$TOPIC\",\"name\":\"$SLUG-intake\",\"type\":\"event_api\",\"event_types\":[\"payment.created\"],\"configuration\":{},\"verification\":null,\"input_requirements\":null,\"mapping\":null,\"event_identity_rule\":null}" | jq -er .id)
SUBSCRIPTION=$(post "/admin/tenants/$TENANT/topics/$TOPIC/subscriptions" \
  "{\"name\":\"$SLUG-to-sink\",\"event_types\":[\"payment.created\"],\"destination_id\":\"$DESTINATION\",\"mapping\":null,\"http_delivery\":null,\"http_success\":null,\"order_index\":0}" | jq -er .id)
post "/admin/tenants/$TENANT/sources/$SOURCE/activate" '' >/dev/null
post "/admin/tenants/$TENANT/topics/$TOPIC/subscriptions/$SUBSCRIPTION/activate" '' >/dev/null

cat <<EOF
export SLUG=$SLUG CONNECTOR=$CONNECTOR TENANT=$TENANT TOKEN=$TOKEN
export DESTINATION=$DESTINATION TOPIC=$TOPIC SOURCE=$SOURCE SUBSCRIPTION=$SUBSCRIPTION
EOF
