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

echo 'PASS: environment quoting, mandatory build, delayed shutdown, shutdown timeout'
