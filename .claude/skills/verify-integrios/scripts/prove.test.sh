#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/prove.sh"
bash "$SCRIPTS/prove.sh" --help >/dev/null
if bash "$SCRIPTS/prove.sh" unknown-command >/dev/null 2>&1; then
  echo 'Unknown command was accepted' >&2; exit 1
fi
proof=$(mktemp -d)
trap 'rm -rf "$proof"' EXIT
printf '%s\n' '{"status":"accepted"}' >"$proof/response.json"
assert_json 'valid response' "$proof/response.json" '.status == "accepted"'
if assert_json 'wrong response' "$proof/response.json" '.status == "succeeded"' 2>"$proof/expected-json-error"; then
  echo 'Incorrect JSON passed verification' >&2; exit 1
fi
curl() { printf 401; }
if request unauthorized 202 http://unused.invalid 2>"$proof/expected-status-error"; then
  echo 'Incorrect HTTP status passed verification' >&2; exit 1
fi
jq -se 'length == 3 and ([.[] | select(.passed == false)] | length == 2)' "$proof/assertions.jsonl" >/dev/null
echo 'PASS: proof assertions reject incorrect JSON and HTTP status'
