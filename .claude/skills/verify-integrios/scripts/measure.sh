#!/usr/bin/env bash
# Runs the Delivery API design's load checks on SQL Server with two Workers, each run on a fresh
# stack so no stalled backlog carries over: fault injection alternating push and pull, then intake
# under Subscriber load with the Workers stopped. Results land in artifacts/verify/measure/<stamp>/.
# Usage: measure.sh [runs-per-side | intake]   (default 5; "intake" runs only the intake check;
# MEASURE_* variables pass through to measure.mjs)
set -euo pipefail

V=$(cd "$(dirname "$0")" && pwd)
ROOT=$(git rev-parse --show-toplevel)
RUNS=${1:-5}
[ "$RUNS" = intake ] && RUNS=0
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
OUT=$ROOT/artifacts/verify/measure/$STAMP
export VERIFY_DB=sqlserver VERIFY_WORKERS=2 VERIFY_PORT_OFFSET=${VERIFY_PORT_OFFSET:-22000}
mkdir -p "$OUT"
{ echo "commit $(git -C "$ROOT" rev-parse HEAD)"; echo "tracked diff $(git -C "$ROOT" diff HEAD | sha256sum | cut -c1-16)"
  echo "runs per side $RUNS"; env | grep '^MEASURE_' || true; } >"$OUT/run.txt"

# Role logs live in the state directory, which down deletes; a failed run tears down on exit.
teardown() {
  mkdir -p "$OUT/$1"
  cp "$ROOT/artifacts/verify/$VERIFY_RUN/state/"*.log "$OUT/$1/" 2>/dev/null || true
  "$V/control.sh" down >>"$OUT/$1.down.log" 2>&1
}
current=
trap '[ -z "$current" ] || teardown "$current"' EXIT
stack() {
  # A run name of its own, so a failed start never tears down another invocation's stack.
  export VERIFY_RUN=measure-${STAMP,,}-$1
  current=$1
  "$V/control.sh" up >"$OUT/$1.up.log" 2>&1 || { cat "$OUT/$1.up.log" >&2; exit 1; }
  # shellcheck disable=SC1090
  source "$ROOT/artifacts/verify/$VERIFY_RUN/state/env"
}

for ((i = 1; i <= RUNS; i++)); do
  for side in push pull; do
    name=fault-$side-$i
    stack "$name"
    node "$V/measure.mjs" fault "$side" "$OUT/$name" | tee -a "$OUT/progress.jsonl"
    teardown "$name"; current=
  done
done

stack intake
for role in worker worker-2; do kill "$(cat "$ROOT/artifacts/verify/$VERIFY_RUN/state/$role.pid")"; done
sleep 5
node "$V/measure.mjs" intake "$OUT/intake" | tee -a "$OUT/progress.jsonl"
teardown intake; current=
echo "results: $OUT"
