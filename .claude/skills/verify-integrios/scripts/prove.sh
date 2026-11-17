#!/usr/bin/env bash
set -euo pipefail
SCRIPTS=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

record() {
  jq -nc --arg check "$1" --argjson passed "$2" '{check:$check,passed:$passed}' >>"$proof/assertions.jsonl"
  [[ $2 == true ]] || { echo "FAIL: $1; see $proof" >&2; return 1; }
}

request() {
  local name=$1 expected=$2 status; shift 2
  status=$(curl -sS --max-time 10 -o "$proof/$name.json" -w '%{http_code}' "$@")
  printf '%s\n' "$status" >"$proof/$name.status"
  record "$name HTTP $expected" "$([[ $status == "$expected" ]] && echo true || echo false)"
}

assert_json() {
  local name=$1 file=$2 filter=$3; shift 3
  local passed=false
  if jq -e "$@" "$filter" "$file" >/dev/null; then passed=true; fi
  record "$name" "$passed"
}

prove() {
  export VERIFY_RUN=${VERIFY_RUN:-proof-$(date +%s)-$RANDOM}
  source "$SCRIPTS/control.sh"
  cd "$ROOT"
  [[ ! -d $STATE ]] || { echo "Run already exists; choose another VERIFY_RUN." >&2; return 1; }
  proof=$EVIDENCE/proof
  mkdir -p "$proof"
  started=$SECONDS
  revision=$(git rev-parse HEAD)
  tracked_diff=$(git diff HEAD -- | git hash-object --stdin)

  finish() {
    local code=$?
    trap - EXIT
    set +e
    if [[ -d $STATE ]]; then
      mkdir -p "$proof/logs"
      cp "$STATE"/*.log "$proof/logs/" 2>/dev/null
      if ! "$SCRIPTS/control.sh" down >"$proof/cleanup.log" 2>&1; then code=1; fi
    fi
    jq -n --arg revision "$revision" --arg tracked_diff "$tracked_diff" --arg run "$RUN" \
      --argjson exit_code "$code" --argjson seconds "$((SECONDS - started))" \
      '{revision:$revision,tracked_diff:$tracked_diff,run:$run,exit_code:$exit_code,seconds:$seconds,
        scope:["connector response","event intake","HTTP delivery","idempotency","rejected credentials"],
        not_checked:["dashboard","retry/dead-letter/replay","SQL Server","packaged deployment",
          "broker Sources","webhook verification","payload mappings","OIDC"]}' >"$proof/summary.json"
    echo "Proof exit $code; evidence: $proof"
    exit "$code"
  }
  trap finish EXIT

  "$SCRIPTS/control.sh" up >"$proof/launch.log" 2>&1
  source "$STATE/env"
  "$SCRIPTS/control.sh" doctor >"$proof/doctor.log" 2>&1
  eval "$("$SCRIPTS/seed-delivery.sh")"

  request connector 200 -X PUT "$ADMIN/admin/connectors/http/versions/1" -H "$AUTH" \
    -H 'Content-Type: application/json' --data-binary "@$ROOT/examples/connectors/http.json"
  assert_json 'quickstart Connector ID matches seeded Connector' "$proof/connector.json" \
    '.connector.id == $id' --arg id "$CONNECTOR"

  printf '%s\n' '{"event_type":"payment.created","source_event_id":"proof-001","payload":{"amount":1200}}' >"$proof/request.json"
  request accept 202 -X POST "$INGESTION/events?source_id=$SOURCE" -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' --data-binary "@$proof/request.json"
  assert_json 'new Event accepted' "$proof/accept.json" '.status == "accepted" and .already_accepted == false and (.event_id | type == "string")'
  local event deadline
  event=$(jq -er .event_id "$proof/accept.json")
  deadline=$((SECONDS + 30))
  while :; do
    curl -fsS --max-time 5 "$INGESTION/events/$event" -H "Authorization: Bearer $TOKEN" >"$proof/event.json"
    jq -e 'any(.delivery_attempts[]?; .status == "succeeded")' "$proof/event.json" >/dev/null && break
    ((SECONDS < deadline)) || { record 'delivery completed within 30 seconds' false; return 1; }
    sleep 1
  done
  assert_json 'Event read-back matches and is routed' "$proof/event.json" '.event_id == $id and .status == "routed"' --arg id "$event"
  request deliveries 200 "$ADMIN/admin/tenants/$TENANT/events/$event/deliveries" -H "$AUTH"
  assert_json 'Operator sees successful delivery' "$proof/deliveries.json" \
    '.event_deliveries | length == 1 and .[0].status == "succeeded"'

  request duplicate 202 -X POST "$INGESTION/events?source_id=$SOURCE" -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' --data-binary "@$proof/request.json"
  assert_json 'resubmission returns the same Event' "$proof/duplicate.json" '.event_id == $id and .already_accepted == true' --arg id "$event"
  request rejected 401 -X POST "$INGESTION/events?source_id=$SOURCE" -H 'Authorization: Bearer intg_wrong' \
    -H 'Content-Type: application/json' --data-binary "@$proof/request.json"
  assert_json 'rejection does not claim acceptance' "$proof/rejected.json" '.event_id == null'

  request sink 200 -X POST "$MOCKSINK/__admin/requests/find" -H 'Content-Type: application/json' \
    -d "{\"method\":\"POST\",\"urlPath\":\"/sink/$SLUG\"}"
  assert_json 'sink received the expected payload' "$proof/sink.json" \
    '.requests | length == 1 and (.[0].body | fromjson | .amount == 1200)'
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
  case ${1:-} in
    '') prove ;;
    --help|-h) echo 'usage: prove.sh (fresh isolated API proof; VERIFY_RUN and VERIFY_PORT_OFFSET optional)' ;;
    *) echo 'usage: prove.sh' >&2; exit 2 ;;
  esac
fi
