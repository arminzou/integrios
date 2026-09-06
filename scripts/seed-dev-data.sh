#!/usr/bin/env bash
# Replaces the local dev database's tenant-scoped data with a realistic demo dataset, then drives
# real Events through Ingestion so the dashboard shows delivered, unrouted, and dead-lettered work.
#
# Everything is created through the public Admin and Ingestion APIs -- no direct inserts -- so the
# resulting rows are exactly what the platform itself would have written. Only the wipe touches SQL.
#
# Requires the Compose stack (`make up`), curl, jq, and docker compose.
set -euo pipefail

ADMIN=${ADMIN:-http://localhost:5150}
INGESTION=${INGESTION:-http://localhost:5231}
MOCKSINK=${MOCKSINK:-http://localhost:5054}
AUTH="Authorization: OperatorKey ${INTEGRIOS_OPERATOR_KEY:-global_operator_key:operator_bootstrap_secret}"
JSON='Content-Type: application/json'

admin() { # admin METHOD PATH [BODY]
  local method=$1 path=$2 body=${3:-}
  if [ -n "$body" ]; then
    curl -fsS -X "$method" "$ADMIN$path" -H "$AUTH" -H "$JSON" -d "$body"
  else
    curl -fsS -X "$method" "$ADMIN$path" -H "$AUTH"
  fi
}

say() { printf '\n=== %s\n' "$1"; }

psql_q() { docker compose exec -T postgres psql -qtAX -U integrios -d integrios -c "$1"; }

# --- 1. Wipe tenant-scoped data --------------------------------------------------------------
# Connectors and OperatorKeys are deployment-level and survive: they are what bootstrap and the
# manifest apply own, not demo data.
say "clearing existing tenant data"
psql_q "truncate table delivery_attempts, event_deliveries, outbox, events, subscriptions, sources, topics, connections, tenant_api_keys, tenants cascade;"
curl -fsS -X DELETE "$MOCKSINK/__admin/requests" > /dev/null
curl -fsS -X POST "$MOCKSINK/__admin/mappings/reset" > /dev/null

# --- 2. Connector -----------------------------------------------------------------------------
say "applying the http Connector manifest"
CONNECTOR=$(admin PUT /admin/connectors/http/versions/1 "$(cat examples/connectors/http.json)" | jq -r .id)

# --- Helpers ----------------------------------------------------------------------------------
new_tenant() { # slug name environment description
  admin POST /admin/tenants "$(jq -n --arg s "$1" --arg n "$2" --arg e "$3" --arg d "$4" \
    '{slug:$s,name:$n,environment:$e,description:$d}')" | jq -r .id
}

new_connection() { # tenant name base_uri environment description  (empty base_uri = source side)
  local config='{}'
  [ -n "$3" ] && config=$(jq -n --arg u "$3" '{base_uri:$u}')
  admin POST "/admin/tenants/$1/connections" "$(jq -n --arg c "$CONNECTOR" --arg n "$2" \
    --argjson cfg "$config" --arg e "$4" --arg d "$5" \
    '{connector_id:$c,name:$n,config:$cfg,environment:$e,description:$d}')" | jq -r .id
}

new_topic() { # tenant name description
  admin POST "/admin/tenants/$1/topics" "$(jq -n --arg n "$2" --arg d "$3" '{name:$n,description:$d}')" | jq -r .id
}

new_source() { # tenant connection topic
  admin POST "/admin/tenants/$1/sources" "$(jq -n --arg c "$2" --arg t "$3" \
    '{connection_id:$c,topic_id:$t,type:"event_api",configuration:{source_contract:"event_json"}}')" | jq -r .id
}

new_subscription() { # tenant topic name event_type destination order description [mapping_json]
  admin POST "/admin/tenants/$1/topics/$2/subscriptions" "$(jq -n --arg n "$3" --arg et "$4" \
    --arg dst "$5" --argjson o "$6" --arg d "$7" --argjson m "${8:-null}" \
    '{name:$n,match_rules:{event_type:$et},destination_connection_id:$dst,order_index:$o,description:$d,mapping:$m}')" | jq -r .id
}

new_key() { # tenant name description
  admin POST "/admin/tenants/$1/tenant-api-keys" "$(jq -n --arg n "$2" --arg d "$3" '{name:$n,description:$d}')" | jq -r .token
}

send() { # source token event_type source_event_id payload_json
  curl -fsS -X POST "$INGESTION/events?source_id=$1" -H "Authorization: TenantApiKey $2" -H "$JSON" \
    -d "$(jq -n --arg t "$3" --arg i "$4" --argjson p "$5" '{event_type:$t,source_event_id:$i,payload:$p}')" \
    | jq -r .event_id
}

# --- 3. Northwind Retail: the fully-configured Tenant ------------------------------------------
say "Northwind Retail"
NW=$(new_tenant northwind-retail "Northwind Retail" production "Retail commerce platform. Orders, payments, and stock levels flow out to the ERP, the warehouse, and the analytics lake.")
NW_TOKEN=$(new_key "$NW" northwind-storefront "Storefront checkout service. Rotated quarterly.")
NW_OLD_KEY=$(admin POST "/admin/tenants/$NW/tenant-api-keys" '{"name":"northwind-legacy-pos","description":"Retired point-of-sale integration."}' | jq -r .tenant_api_key.id)
admin POST "/admin/tenants/$NW/tenant-api-keys/$NW_OLD_KEY/revoke" > /dev/null

NW_IN=$(new_connection "$NW" storefront-intake "" production "Checkout and fulfilment events from the storefront.")
NW_ERP=$(new_connection "$NW" northwind-erp http://mocksink:8080/sink/northwind-erp production "Order and payment records into the ERP.")
NW_WMS=$(new_connection "$NW" northwind-wms http://mocksink:8080/sink/northwind-wms production "Warehouse management system, fulfilment side only.")
NW_LAKE=$(new_connection "$NW" analytics-lake http://mocksink:8080/sink/northwind-lake production "Flattened order feed for the analytics lake.")
NW_BILLING=$(new_connection "$NW" legacy-billing http://mocksink:8080/sink/northwind-billing production "Decommissioned billing host. Kept until finance signs off on the cutover.")
NW_SANDBOX=$(new_connection "$NW" erp-sandbox http://mocksink:8080/sink/northwind-sandbox staging "Vendor sandbox used during the last ERP upgrade.")
admin POST "/admin/tenants/$NW/connections/$NW_SANDBOX/deactivate" > /dev/null

NW_ORDERS=$(new_topic "$NW" orders "Order lifecycle from the storefront.")
NW_PAY=$(new_topic "$NW" payments "Payment authorisation and capture.")
NW_STOCK=$(new_topic "$NW" inventory "Stock level movements per warehouse.")
NW_OLD=$(new_topic "$NW" pos-terminals "Retired in-store terminal stream.")
admin POST "/admin/tenants/$NW/topics/$NW_OLD/deactivate" > /dev/null

NW_ORDERS_SRC=$(new_source "$NW" "$NW_IN" "$NW_ORDERS")
NW_PAY_SRC=$(new_source "$NW" "$NW_IN" "$NW_PAY")
NW_STOCK_SRC=$(new_source "$NW" "$NW_IN" "$NW_STOCK")

new_subscription "$NW" "$NW_ORDERS" erp-orders order.placed "$NW_ERP" 0 "Every placed order into the ERP." > /dev/null
new_subscription "$NW" "$NW_ORDERS" wms-fulfilment order.shipped "$NW_WMS" 1 "Shipment confirmations back to the warehouse." > /dev/null
new_subscription "$NW" "$NW_ORDERS" lake-orders order.placed "$NW_LAKE" 2 "Flattened order feed for the analytics lake." \
  '{"engine":"jsonata","version":"1","expression":"{ \"order\": payload.orderId, \"total\": payload.total, \"placed_at\": payload.placedAt }"}' > /dev/null
new_subscription "$NW" "$NW_PAY" erp-payments payment.captured "$NW_ERP" 0 "Captured payments into the ERP ledger." > /dev/null
new_subscription "$NW" "$NW_PAY" legacy-billing-feed payment.captured "$NW_BILLING" 1 "Mirror of captured payments into the decommissioned billing host." > /dev/null
new_subscription "$NW" "$NW_STOCK" wms-stock stock.adjusted "$NW_WMS" 0 "Stock adjustments back to the warehouse." > /dev/null
NW_PAUSED=$(new_subscription "$NW" "$NW_STOCK" lake-stock stock.adjusted "$NW_LAKE" 1 "Paused while the lake schema migration runs.")
admin POST "/admin/tenants/$NW/topics/$NW_STOCK/subscriptions/$NW_PAUSED/deactivate" > /dev/null

# --- 4. Helios Energy: smaller, staging -------------------------------------------------------
say "Helios Energy"
HE=$(new_tenant helios-energy "Helios Energy" staging "Metering pilot. Half-hourly readings and device alarms from the field trial.")
HE_TOKEN=$(new_key "$HE" helios-field-gateway "Field gateway in the pilot region.")
HE_IN=$(new_connection "$HE" field-gateway "" staging "Meter and device telemetry from the field gateway.")
HE_OPS=$(new_connection "$HE" ops-console http://mocksink:8080/sink/helios-ops staging "Operations console alarm feed.")
HE_METERS=$(new_topic "$HE" metering "Half-hourly meter readings.")
HE_DEVICES=$(new_topic "$HE" devices "Device health and alarms.")
HE_METERS_SRC=$(new_source "$HE" "$HE_IN" "$HE_METERS")
HE_DEVICES_SRC=$(new_source "$HE" "$HE_IN" "$HE_DEVICES")
new_subscription "$HE" "$HE_DEVICES" ops-alarms device.alarm.raised "$HE_OPS" 0 "Raised alarms to the operations console." > /dev/null

# --- 5. Atlas Logistics: production, one unstable partner ---------------------------------------
say "Atlas Logistics"
AT=$(new_tenant atlas-logistics "Atlas Logistics" production "Freight tracking. Consignment scans out to the customer portal and the partner carrier API.")
AT_TOKEN=$(new_key "$AT" atlas-scanners "Depot handheld scanners.")
AT_IN=$(new_connection "$AT" depot-scanners "" production "Consignment scan events from the depots.")
AT_PORTAL=$(new_connection "$AT" customer-portal http://mocksink:8080/sink/atlas-portal production "Tracking updates shown to customers.")
AT_CARRIER=$(new_connection "$AT" partner-carrier http://mocksink:8080/sink/atlas-carrier production "Partner carrier handover API. Their sandbox is unstable.")
AT_FREIGHT=$(new_topic "$AT" consignments "Consignment scan and status events.")
AT_SRC=$(new_source "$AT" "$AT_IN" "$AT_FREIGHT")
new_subscription "$AT" "$AT_FREIGHT" portal-tracking consignment.scanned "$AT_PORTAL" 0 "Scan events to the customer tracking page." > /dev/null
new_subscription "$AT" "$AT_FREIGHT" carrier-handover consignment.handover "$AT_CARRIER" 1 "Handover notifications to the partner carrier." > /dev/null

# --- 6. Pilotworks: deactivated Tenant ---------------------------------------------------------
say "Pilotworks"
PW=$(new_tenant pilotworks "Pilotworks" development "Evaluation Tenant from the Q1 proof of concept. Kept for its configuration; no longer sending.")
PW_IN=$(new_connection "$PW" poc-intake "" development "Proof-of-concept intake.")
PW_TOPIC=$(new_topic "$PW" trials "Proof-of-concept event stream.")
new_source "$PW" "$PW_IN" "$PW_TOPIC" > /dev/null
admin POST "/admin/tenants/$PW/deactivate" > /dev/null

# --- 7. Make two destinations fail so Deliveries dead-letter ------------------------------------
say "pointing two destinations at a failing sink"
# The path is passed to jq without its leading slash: Git Bash would otherwise rewrite a
# leading-slash argument into a Windows path before the native jq binary ever sees it.
for path in northwind-billing atlas-carrier; do
  curl -fsS -X POST "$MOCKSINK/__admin/mappings" -H "$JSON" -d "$(jq -n --arg p "sink/$path" \
    '{priority:1,request:{method:"POST",urlPath:("/" + $p)},response:{status:503,jsonBody:{error:"upstream unavailable"}}}')" > /dev/null
done

# --- 8. Real traffic ----------------------------------------------------------------------------
say "sending events"
for i in $(seq 1 14); do
  send "$NW_ORDERS_SRC" "$NW_TOKEN" order.placed "nw-order-$i" \
    "$(jq -n --arg id "SO-40$i" --argjson n "$i" '{orderId:$id,customer:"Contoso Ltd",lines:$n,total:(119.5 * $n),currency:"GBP",placedAt:"2026-09-05T09:12:00Z"}')" > /dev/null
done
for i in $(seq 1 6); do
  send "$NW_ORDERS_SRC" "$NW_TOKEN" order.shipped "nw-ship-$i" \
    "$(jq -n --arg id "SO-40$i" '{orderId:$id,carrier:"Atlas Logistics",tracking:"ATL9911772",warehouse:"LEE-01"}')" > /dev/null
done
for i in $(seq 1 9); do
  send "$NW_PAY_SRC" "$NW_TOKEN" payment.captured "nw-pay-$i" \
    "$(jq -n --arg id "pay_8f2$i" --argjson n "$i" '{paymentId:$id,amount:(119.5 * $n),currency:"GBP",method:"card",processor:"stripe"}')" > /dev/null
done
for i in $(seq 1 5); do
  send "$NW_STOCK_SRC" "$NW_TOKEN" stock.adjusted "nw-stock-$i" \
    "$(jq -n --arg s "SKU-77$i" --argjson n "$i" '{sku:$s,warehouse:"LEE-01",delta:(0 - $n),reason:"pick"}')" > /dev/null
done
# No Subscription matches these: they land as unrouted, the signal for a missing Subscription.
for i in $(seq 1 3); do
  send "$NW_ORDERS_SRC" "$NW_TOKEN" order.cancelled "nw-cancel-$i" \
    "$(jq -n --arg id "SO-41$i" '{orderId:$id,reason:"customer_request"}')" > /dev/null
done

for i in $(seq 1 8); do
  send "$HE_METERS_SRC" "$HE_TOKEN" meter.reading "he-read-$i" \
    "$(jq -n --arg m "MTR-3310$i" --argjson n "$i" '{meterId:$m,kwh:(1.4 * $n),readAt:"2026-09-05T08:30:00Z",tariff:"economy7"}')" > /dev/null
done
for i in $(seq 1 4); do
  send "$HE_DEVICES_SRC" "$HE_TOKEN" device.alarm.raised "he-alarm-$i" \
    "$(jq -n --arg m "MTR-3310$i" '{meterId:$m,alarm:"tamper_detected",severity:"high"}')" > /dev/null
done

for i in $(seq 1 11); do
  send "$AT_SRC" "$AT_TOKEN" consignment.scanned "at-scan-$i" \
    "$(jq -n --arg c "CN-5512$i" '{consignment:$c,depot:"MAN-03",scan:"inbound",scannedAt:"2026-09-05T06:44:00Z"}')" > /dev/null
done
for i in $(seq 1 4); do
  send "$AT_SRC" "$AT_TOKEN" consignment.handover "at-hand-$i" \
    "$(jq -n --arg c "CN-5512$i" '{consignment:$c,partner:"Meridian Freight",manifest:"MF-2209"}')" > /dev/null
done

# --- 9. Wait for the failing Deliveries to exhaust their retries ---------------------------------
# Three attempts on a 30s exponential base, so dead-lettering lands ~90s after the first attempt.
say "waiting for the failing Deliveries to dead-letter (about two minutes)"
for _ in $(seq 1 40); do
  dead=$(psql_q "select count(*) from event_deliveries where status = 'dead_lettered';")
  printf '  dead-lettered: %s\n' "$dead"
  [ "$dead" -ge 13 ] && break
  sleep 10
done

say "done"
docker compose exec -T postgres psql -U integrios -d integrios -c \
  "select t.slug, e.status event_status, count(*) from events e join tenants t on t.id = e.tenant_id group by 1,2 order by 1,2;"
docker compose exec -T postgres psql -U integrios -d integrios -c \
  "select status, count(*) from event_deliveries group by 1 order by 1;"
printf '\nDashboard: %s   (operator key: global_operator_key:operator_bootstrap_secret)\n' "$ADMIN"
