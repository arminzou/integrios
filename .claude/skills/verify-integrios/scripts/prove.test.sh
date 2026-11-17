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
printf '%s\n' '{"payload":{"amount":1200,"currency":"USD"}}' >"$proof/request.json"
printf '%s\n' '{"requests":[{"body":"{\"currency\":\"USD\",\"amount\":1200}"}]}' >"$proof/sink.json"
assert_sink_payload "$proof/sink.json" "$proof/request.json"
for payload in '{"amount":1200}' '{"amount":1200,"currency":"USD","extra":true}' '{"amount":1200,"currency":"EUR"}'; do
  jq -n --argjson payload "$payload" '{requests:[{body:($payload|tojson)}]}' >"$proof/sink.json"
  if assert_sink_payload "$proof/sink.json" "$proof/request.json" 2>"$proof/expected-payload-error"; then
    echo 'Incorrect delivered payload passed verification' >&2; exit 1
  fi
done
echo 'PASS: proof assertions reject incorrect JSON, HTTP status, and missing/extra/changed payload fields'
