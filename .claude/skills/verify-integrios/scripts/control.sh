#!/usr/bin/env bash
# Launches, inspects, and tears down one isolated Integrios verification stack.
# Usage: control.sh <up [--dashboard] | doctor | env | integrios <args...> | down>
set -euo pipefail

ROOT=$(git rev-parse --show-toplevel)
RUN=${VERIFY_RUN:-default}
OFFSET=${VERIFY_PORT_OFFSET:-20000}
RUN_DIR=$ROOT/artifacts/verify/$RUN
STATE=$RUN_DIR/state
EVIDENCE=$RUN_DIR/evidence
PROJECT=integrios-verify-$RUN
BIN=$ROOT/src/Integrios/bin/Debug/net10.0/integrios.dll

PG_PORT=$((5432 + OFFSET))
MOCKSINK_PORT=$((5054 + OFFSET))
ADMIN_PORT=$((5150 + OFFSET))
ADMIN_OP_PORT=$((5151 + OFFSET))
INGESTION_PORT=$((5231 + OFFSET))
INGESTION_OP_PORT=$((5232 + OFFSET))
WORKER_OP_PORT=$((5299 + OFFSET))
PORTS="$PG_PORT $MOCKSINK_PORT $ADMIN_PORT $ADMIN_OP_PORT $INGESTION_PORT $INGESTION_OP_PORT $WORKER_OP_PORT"

OPERATOR_KEY=global_operator_key:operator_bootstrap_secret
OPERATOR_EMAIL=operator@example.test
# 127.0.0.1, never localhost: on Windows with Docker, localhost adds seconds to every cold
# connection, which outlasts the 5-second database readiness check.
pg() { echo "Host=127.0.0.1;Port=$PG_PORT;Database=integrios;Username=$1;Password=$2;Include Error Detail=true"; }
OWNER_DB=$(pg integrios integrios_dev)
ADMIN_DB=$(pg admin_runtime admin_runtime_dev)
DATA_DB=$(pg data_runtime data_runtime_dev)

compose() {
  INTEGRIOS_POSTGRES_PORT=$PG_PORT INTEGRIOS_MOCKSINK_PORT=$MOCKSINK_PORT \
    docker compose -p "$PROJECT" -f "$ROOT/compose.yml" "$@"
}

port_open() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

integrios() {
  DOTNET_ENVIRONMENT=Development Integrios__PublicIngestionBaseUri=http://127.0.0.1:$INGESTION_PORT \
    dotnet "$BIN" "$@"
}

print_env() {
  cat <<EOF
export VERIFY_RUN=$RUN VERIFY_PORT_OFFSET=$OFFSET
export ADMIN=http://127.0.0.1:$ADMIN_PORT INGESTION=http://127.0.0.1:$INGESTION_PORT MOCKSINK=http://127.0.0.1:$MOCKSINK_PORT
export ADMIN_OPS=http://127.0.0.1:$ADMIN_OP_PORT INGESTION_OPS=http://127.0.0.1:$INGESTION_OP_PORT WORKER_OPS=http://127.0.0.1:$WORKER_OP_PORT
export AUTH='Authorization: OperatorKey $OPERATOR_KEY'
export OPERATOR_EMAIL=$OPERATOR_EMAIL
EOF
  printf 'export OPERATOR_PASSWORD_FILE=%q\n' "$STATE/operator-password"
  printf 'export EVIDENCE=%q\n' "$EVIDENCE"
}

start_role() {
  local role=$1 cwd=$2; shift 2
  (cd "$cwd" && export "$@" DOTNET_ENVIRONMENT=Development &&
    { dotnet "$BIN" "$role" >"$STATE/$role.log" 2>&1 & echo $! >"$STATE/$role.pid"; })
}

wait_ready() {
  local name=$1 url=$2 deadline=$((SECONDS + 60)) remaining
  while (( (remaining = deadline - SECONDS) > 0 )); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' -m "$((remaining < 10 ? remaining : 10))" "$url" || true)" = 200 ] && { echo "ready: $name"; return; }
    if ((SECONDS < deadline)); then sleep 1; fi
  done
  echo "not ready after 60s: $name ($url); see $STATE/$name.log" >&2
  return 1
}

up() {
  if [ -d "$STATE" ]; then echo "run '$RUN' already has state at $STATE; run down first or pick another VERIFY_RUN" >&2; exit 1; fi
  for p in $PORTS; do
    if port_open "$p"; then echo "port $p is taken; choose another VERIFY_PORT_OFFSET" >&2; exit 1; fi
  done
  mkdir -p "$STATE" "$EVIDENCE"

  compose up -d --wait postgres mocksink
  # One build at a time per worktree: parallel builds collide in obj/.
  dotnet build "$ROOT/src/Integrios/Integrios.csproj" -nologo -v q
  git -C "$ROOT" rev-parse HEAD >"$STATE/revision"
  if [ "${1:-}" = --dashboard ]; then
    local frontend=$ROOT/src/Integrios.Admin/frontend
    [ -d "$frontend/node_modules" ] || npm --prefix "$frontend" ci
    npm --prefix "$frontend" run build:assets
  fi

  ConnectionStrings__Postgres=$OWNER_DB integrios admin database migrate
  ConnectionStrings__Postgres=$OWNER_DB \
    Database__RuntimePrincipals__0__Name=admin_runtime Database__RuntimePrincipals__0__Scope=control-plane \
    Database__RuntimePrincipals__0__Password=admin_runtime_dev \
    Database__RuntimePrincipals__1__Name=data_runtime Database__RuntimePrincipals__1__Scope=data-plane \
    Database__RuntimePrincipals__1__Password=data_runtime_dev \
    integrios admin database grant-runtime
  ConnectionStrings__Postgres=$ADMIN_DB INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET=${OPERATOR_KEY#*:} integrios admin bootstrap
  printf %s "verify-$(date +%s)-$RANDOM-operator" >"$STATE/operator-password"
  ConnectionStrings__Postgres=$ADMIN_DB integrios admin operator-user create \
    --display-name "Verification Operator" --email "$OPERATOR_EMAIL" --password-file "$STATE/operator-password"

  # Admin runs from its project directory so the dashboard's wwwroot is its web root.
  start_role admin "$ROOT/src/Integrios.Admin" ConnectionStrings__Postgres="$ADMIN_DB" \
    ASPNETCORE_URLS=http://127.0.0.1:$ADMIN_PORT OperationalPort=$ADMIN_OP_PORT \
    Integrios__PublicIngestionBaseUri=http://127.0.0.1:$INGESTION_PORT \
    Integrios__Admin__DataProtection__KeyRingPath="$STATE/data-protection"
  start_role ingestion "$ROOT" ConnectionStrings__Postgres="$DATA_DB" \
    ASPNETCORE_URLS=http://127.0.0.1:$INGESTION_PORT OperationalPort=$INGESTION_OP_PORT
  start_role worker "$ROOT" ConnectionStrings__Postgres="$DATA_DB" OperationalPort=$WORKER_OP_PORT

  wait_ready admin "http://127.0.0.1:$ADMIN_OP_PORT/ready"
  wait_ready ingestion "http://127.0.0.1:$INGESTION_OP_PORT/ready"
  wait_ready worker "http://127.0.0.1:$WORKER_OP_PORT/ready"
  print_env >"$STATE/env"
  echo "up: run '$RUN'; load it with: source $STATE/env"
}

doctor() {
  local failed=0
  check() { if eval "$2" >/dev/null 2>&1; then echo "ok   $1"; else echo "FAIL $1"; failed=1; fi; }
  check "state dir $STATE" "[ -f '$STATE/env' ]"
  for role in admin ingestion worker; do
    check "$role process from this run is alive" "kill -0 \$(cat '$STATE/$role.pid')"
  done
  check "admin /ready" "curl -fsS -m 10 http://127.0.0.1:$ADMIN_OP_PORT/ready"
  check "ingestion /ready" "curl -fsS -m 10 http://127.0.0.1:$INGESTION_OP_PORT/ready"
  check "worker /ready" "curl -fsS -m 10 http://127.0.0.1:$WORKER_OP_PORT/ready"
  check "OperatorKey accepted by Admin API" "curl -fsS -m 10 -H 'Authorization: OperatorKey $OPERATOR_KEY' http://127.0.0.1:$ADMIN_PORT/admin/tenants"
  check "password sign-in enabled" "curl -fsS -m 10 http://127.0.0.1:$ADMIN_PORT/auth/options | grep -q '\"password_enabled\":true'"
  check "mocksink answers" "curl -fsS -m 10 http://127.0.0.1:$MOCKSINK_PORT/__admin/health"
  check "postgres container belongs to $PROJECT" "compose ps --status running -q postgres | grep -q ."
  check "built from current HEAD $(git -C "$ROOT" rev-parse --short HEAD)" "[ \"\$(cat '$STATE/revision')\" = \"\$(git -C '$ROOT' rev-parse HEAD)\" ]"
  if [ -f "$ROOT/src/Integrios.Admin/wwwroot/index.html" ]; then echo "ok   dashboard assets built"; else echo "info dashboard assets absent (up --dashboard builds them)"; fi
  return $failed
}

down() {
  local role pid attempt
  for role in admin ingestion worker; do
    [ -f "$STATE/$role.pid" ] && kill "$(cat "$STATE/$role.pid")" 2>/dev/null || true
  done
  for role in admin ingestion worker; do
    [ -f "$STATE/$role.pid" ] || continue
    pid=$(cat "$STATE/$role.pid")
    for ((attempt=0; attempt<75; attempt++)); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 1
    done
    if kill -0 "$pid" 2>/dev/null; then
      echo "$role (PID $pid) did not stop; database and state retained at $STATE; retry down after it exits" >&2
      return 1
    fi
  done
  compose down -v --remove-orphans
  rm -rf "$STATE"
  echo "down: run '$RUN' removed; evidence kept at $EVIDENCE"
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
case ${1:-} in
  up) shift; up "$@" ;;
  doctor) doctor ;;
  env) print_env ;;
  integrios) shift; ConnectionStrings__Postgres=${ConnectionStrings__Postgres:-$OWNER_DB} integrios "$@" ;;
  down) down ;;
  *) echo "usage: $0 <up [--dashboard] | doctor | env | integrios <args...> | down>" >&2; exit 2 ;;
esac
fi
