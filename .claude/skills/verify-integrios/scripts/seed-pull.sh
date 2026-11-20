#!/usr/bin/env bash
# Seeds a fresh Tenant as seed-delivery.sh does, plus a pull Destination on the built-in subscriber
# Connector, a SubscriberKey for it, and an active Subscription for payment.created on the same
# Topic. Prints exports; load them with: eval "$(seed-pull.sh)".
# Requires the run's env (source artifacts/verify/<run>/state/env).
set -euo pipefail

: "${ADMIN:?source the run env first}"
J='Content-Type: application/json'
SUBSCRIBER_CONNECTOR=5ab5c71b-e000-4000-8000-000000000001

seed=$("$(dirname "$0")/seed-delivery.sh")
eval "$seed"
post() { curl -fsS --max-time 10 -X POST "$ADMIN$1" -H "$AUTH" -H "$J" -d "$2"; }

PULL_DESTINATION=$(post "/admin/tenants/$TENANT/destinations" \
  "{\"connector_id\":\"$SUBSCRIBER_CONNECTOR\",\"name\":\"$SLUG-pull\",\"configuration\":{}}" | jq -er .id)
SUBSCRIBER_KEY=$(post "/admin/tenants/$TENANT/destinations/$PULL_DESTINATION/subscriber-keys" '{"name":"verify-subscriber"}' | jq -er .token)
PULL_SUBSCRIPTION=$(post "/admin/tenants/$TENANT/topics/$TOPIC/subscriptions" \
  "{\"name\":\"$SLUG-to-pull\",\"event_types\":[\"payment.created\"],\"destination_id\":\"$PULL_DESTINATION\",\"mapping\":null,\"http_delivery\":null,\"http_success\":null,\"order_index\":1}" | jq -er .id)
post "/admin/tenants/$TENANT/topics/$TOPIC/subscriptions/$PULL_SUBSCRIPTION/activate" '' >/dev/null

echo "$seed"
cat <<EOS
export PULL_DESTINATION=$PULL_DESTINATION SUBSCRIBER_KEY=$SUBSCRIBER_KEY PULL_SUBSCRIPTION=$PULL_SUBSCRIPTION
EOS
