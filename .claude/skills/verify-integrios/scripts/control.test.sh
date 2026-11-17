#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/control.sh"

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

(
  STATE="$scratch/checkout with spaces/state"
  EVIDENCE="$scratch/checkout with spaces/evidence"
  expected_password=$STATE/operator-password
  expected_evidence=$EVIDENCE
  print_env >"$scratch/env"
  unset OPERATOR_PASSWORD_FILE EVIDENCE
  source "$scratch/env"
  [[ $OPERATOR_PASSWORD_FILE == "$expected_password" ]] || fail 'password path changed'
  [[ $EVIDENCE == "$expected_evidence" ]] || fail 'evidence path changed'
)

(
  STATE=$scratch/build/state
  EVIDENCE=$scratch/build/evidence
  port_open() { return 1; }
  compose() { :; }
  dotnet() {
    [[ ! -f $STATE/revision ]] || fail 'revision recorded before build succeeded'
    touch "$scratch/built"
  }
  integrios() { :; }
  start_role() { :; }
  wait_ready() { :; }
  VERIFY_NO_BUILD=1 up >/dev/null
  [[ -f $scratch/built && -s $STATE/revision ]] || fail 'build skipped or revision missing'
)

(
  STATE=$scratch/stop/state
  EVIDENCE=$scratch/stop/evidence
  mkdir -p "$STATE" "$EVIDENCE"
  printf 123 >"$STATE/worker.pid"
  touch "$EVIDENCE/proof"
  probes=0
  kill() {
    [[ $1 == -0 ]] || return 0
    probes=$((probes + 1))
    ((probes < 3))
  }
  sleep() { :; }
  compose() { ((probes >= 3)) || fail 'database removed before role exited'; }
  down >/dev/null
  [[ ! -d $STATE && -f $EVIDENCE/proof ]] || fail 'state remains or evidence removed'
)

(
  STATE=$scratch/timeout/state
  EVIDENCE=$scratch/timeout/evidence
  mkdir -p "$STATE"
  printf 123 >"$STATE/worker.pid"
  kill() { return 0; }
  sleep() { :; }
  compose() { touch "$scratch/database-removed"; }
  if down >"$scratch/timeout.log" 2>&1; then fail 'cleanup accepted a live role'; fi
  [[ -f $STATE/worker.pid && ! -f $scratch/database-removed ]] || fail 'timeout destroyed state or database'
)

(
  curl() {
    while (($#)); do
      if [[ $1 == -m ]]; then printf '%s\n' "$2" >>"$scratch/readiness-budgets"; fi
      shift
    done
    printf 503
  }
  sleep() { SECONDS=$((SECONDS + 27)); }
  SECONDS=0
  if wait_ready test http://unused.invalid >"$scratch/readiness.log" 2>&1; then
    fail 'unready role passed readiness'
  fi
  mapfile -t budgets <"$scratch/readiness-budgets"
  ((${#budgets[@]} <= 3)) || fail 'readiness exceeded elapsed deadline'
  if ((${#budgets[@]} == 3)); then
    ((budgets[2] > 0 && budgets[2] <= 6)) || fail 'readiness request exceeded remaining budget'
  fi
)

(
  curl() { printf 200; }
  sleep() { fail 'ready role kept polling'; }
  wait_ready test http://unused.invalid >/dev/null
)

(
  ADMIN=http://unused.invalid AUTH=test-only MOCKSINK=http://sink.invalid
  curl() {
    [[ " $* " == *' --max-time 10 '* ]] || fail 'seed HTTP request has no timeout'
    touch "$scratch/seed-request"
    printf '%s\n' '{"connector":{"id":"test-id"},"id":"test-id","token":"test-only-token"}'
  }
  source "$(dirname "$0")/seed-delivery.sh" >/dev/null
  [[ -f $scratch/seed-request ]] || fail 'seed sent no requests'
)

echo 'PASS: environment quoting, mandatory build, shutdown, readiness deadline, seed request timeouts'
