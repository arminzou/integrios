# Walkthrough: GitHub to Slack

Drive a verified GitHub webhook through Integrios and deliver a transformed Slack message,
entirely through the Admin and Ingestion APIs. This exercises the platform's generic
verified-webhook Source capability, Operator-authored Connectors, and HTTP success-rule
evaluation on one concrete path — it is not a GitHub-specific or Slack-specific runtime feature.

This walkthrough is API-driven. There is no Operator UI yet; every step below is a `curl` command
against Admin, plus two manual steps in GitHub's and Slack's own consoles that Integrios does not
and will not automate (see [Non-goals](#non-goals)).

## Prerequisites

- The local stack running (`make up`; see [setup.md](setup.md)) with a public HTTPS URL that
  GitHub can reach. A local Compose stack is not reachable from GitHub's servers — use a tunnel
  (for example `ngrok http 5231`) and set `Integrios__PublicIngestionBaseUri` to that public HTTPS
  origin before creating the source endpoint below, or deploy Ingestion somewhere public first. See
  [setup.md](setup.md#production-deployment) for a production reference.
- A GitHub repository or organization you can add a webhook to.
- A Slack workspace and a bot token (`xoxb-...`) with the `chat:write` scope, invited to the
  channel you want messages posted to. Integrios never performs Slack's interactive OAuth
  installation flow; the Operator creates the bot token in Slack's own app console.

## Non-goals

Integrios does not provision or manage the GitHub webhook, does not perform GitHub App
installation, and does not perform Slack's interactive OAuth flow. The Operator configures both
provider sides manually, once, using values this walkthrough produces. This matches the shipped
model: [architecture.md](architecture.md) describes why (no runtime plugins, no broad provider
set).

## 1. Apply the example Connector manifests

The [`examples/connectors/`](../examples/connectors/) directory carries the exact
machine-validated manifests this walkthrough uses. `github.json` declares bounded webhook
verification choices; `slack.json` declares generic HTTP delivery with bearer-token
authentication. The concrete Source mapping and Slack success rule are authored on the Tenant
resources below. Apply is idempotent — a missing version is created, and
re-applying the identical manifest is a no-op.

Apply returns the created Connector, including its deployment-wide `id`, which Sources and
Destinations reference below.

```bash
ADMIN=http://localhost:5150
# GitHub must be able to reach this origin -- see Prerequisites (a tunnel such as ngrok, or a
# real production deployment). It is not the same as ADMIN above.
INGESTION=http://localhost:5231
AUTH="Authorization: OperatorKey global_operator_key:operator_bootstrap_secret"

GITHUB_CONNECTOR=$(curl -s -X PUT "$ADMIN/admin/connectors/github/versions/1" -H "$AUTH" \
  -H 'Content-Type: application/json' --data-binary @examples/connectors/github.json | jq -r .id)

SLACK_CONNECTOR=$(curl -s -X PUT "$ADMIN/admin/connectors/slack/versions/1" -H "$AUTH" \
  -H 'Content-Type: application/json' --data-binary @examples/connectors/slack.json | jq -r .id)
```

Bootstrap installs no Connectors. These are ordinary Operator-authored Connectors, validated by
the same manifest parser and authoring rules any Operator-authored manifest goes through.

## 2. Create a Tenant

```bash
TENANT=$(curl -s -X POST "$ADMIN/admin/tenants" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"slug":"acme","name":"Acme","environment":"production"}' | jq -r .id)
```

## 3. Create a Topic and GitHub webhook Source

The Source selects the `hmac_sha256` verification scheme and names its secret reference; it does
not carry the secret value itself. A different GitHub signing secret requires a different Source.

```bash
TOPIC=$(curl -s -X POST "$ADMIN/admin/tenants/$TENANT/topics" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d '{"key":"github-events","name":"GitHub events"}' | jq -r .id)
```

Give Ingestion the shared secret as a key-per-file file named
`SourceSecrets__<tenant-slug>__<secret-reference>`, then restart it, because secrets load at
startup. Generate the value and keep it; GitHub needs the same value in step 5:

```bash
GITHUB_SECRET=$(openssl rand -hex 32)
printf '%s' "$GITHUB_SECRET" > ./secrets/sources/SourceSecrets__acme__github-webhook-secret
docker compose restart ingestion
```

(See [setup.md](setup.md#tenant-secrets) for the Worker-side convention and the other ways to
supply the same key, such as environment variables or Azure Key Vault.)

## 4. Create the webhook Source and callback URL

A Topic on its own accepts nothing; a **Source** binds one Connector to one Topic and authorizes it
to publish there. Creating a `webhook` Source mints a stable `callback_id` — the sole routing
coordinate GitHub's requests carry. Disabling and enabling the Source keeps it; deleting and
re-creating the Source later mints a new `callback_id`.

A Source also declares the Event types it may publish. Integrios refuses any other type from it, and
Subscriptions on the Topic choose from these declarations. This mapping produces one type per GitHub
event, so declare each one you expect: `github.push` for the flow below, and `github.ping`, which
GitHub sends once when the webhook is created.

```bash
GITHUB_MAPPING='{"event_type":"github." & $context.headers."x-github-event","payload":$}'
SOURCE_JSON=$(jq -n --arg connector "$GITHUB_CONNECTOR" --arg topic "$TOPIC" --arg mapping "$GITHUB_MAPPING" \
  '{connector_id:$connector,topic_id:$topic,name:"GitHub webhook",type:"webhook",
    event_types:["github.push","github.ping"],configuration:{},
    verification:{scheme:"hmac_sha256",config:{},secret_refs:{secret:"github-webhook-secret"}},
    input_requirements:null,mapping:{engine:"jsonata",version:"1",expression:$mapping},
    event_identity_rule:{kind:"header",value:"X-GitHub-Delivery"}}' \
  | curl -s -X POST "$ADMIN/admin/tenants/$TENANT/sources" -H "$AUTH" \
      -H 'Content-Type: application/json' --data-binary @-)
SOURCE=$(echo "$SOURCE_JSON" | jq -r .id)
CALLBACK_ID=$(echo "$SOURCE_JSON" | jq -r .configuration.callback_id)

CALLBACK_URL="$INGESTION/webhooks/$CALLBACK_ID"
echo "$CALLBACK_URL"
```

There is no endpoint that renders the full callback URL for you — build it yourself from `$INGESTION`
(the same GitHub-reachable HTTPS origin from [Prerequisites](#prerequisites)) and the returned
`callback_id`. Set `INGESTION=<your public HTTPS origin>` alongside `$ADMIN` at the top of this
walkthrough if you haven't already.

## 5. Configure the GitHub webhook (manual, external)

In the GitHub repository or organization's **Settings → Webhooks → Add webhook**:

- **Payload URL**: the `callback_url` from step 4.
- **Content type**: `application/json` — this Source's JSONata mapping reads GitHub's JSON body
  and event header; GitHub's form-encoded content type is not accepted.
- **Secret**: the exact value of `$GITHUB_SECRET` from step 3.
- **Events**: at minimum, "Just the push event" is enough to exercise this walkthrough end to end.

GitHub sends a `ping` request immediately after you save the webhook. Integrios accepts it as an
ordinary `github.ping` Event — there is nothing special to check for it, and it will appear in the
Event history alongside real pushes.

## 6. Create the Slack Destination

`base_uri` is Slack's API base; the Subscription (step 8) supplies the relative
`chat.postMessage` path.

```bash
SLACK_DESTINATION=$(curl -s -X POST "$ADMIN/admin/tenants/$TENANT/destinations" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"connector_id\":\"$SLACK_CONNECTOR\",\"name\":\"acme-slack\",
       \"configuration\":{\"base_uri\":\"https://slack.com/api\"},
       \"authentication\":{\"scheme\":\"bearer_token\",\"config\":{},
         \"secret_refs\":{\"token\":\"slack-bot-token\"}},
       \"environment\":\"production\"}" | jq -r .id)

printf '%s' 'xoxb-REPLACE-WITH-YOUR-BOT-TOKEN' > ./secrets/destinations/DestinationSecrets__acme__slack-bot-token
docker compose restart worker
```

A leading or trailing newline in this file (for example from re-saving it in an editor with "insert
final newline" on) is trimmed automatically — it's never a legitimate byte of the secret, so the
platform strips it rather than failing. A line break *inside* the value is different: that's
genuine corruption and still fails closed, surfacing as a `request_construction` delivery failure
with `Auth secret field 'token' contains a line break`.

## 7. Subscribe GitHub pushes to the Slack Destination

The transform is a JSONata expression evaluated with the Event payload as its root and platform
metadata bound to `$context`; `http_delivery` supplies the method, relative path, and body shape,
all owned by the Subscription rather than the Destination.

```bash
SUBSCRIPTION=$(curl -s -X POST "$ADMIN/admin/tenants/$TENANT/topics/$TOPIC/subscriptions" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"push-to-slack\",
       \"event_types\":[\"github.push\"],
       \"destination_id\":\"$SLACK_DESTINATION\",
       \"order_index\":0,
       \"mapping\":{\"engine\":\"jsonata\",\"version\":\"1\",
         \"expression\":\"{'channel': '#deploys', 'text': pusher.name & ' pushed to ' & repository.full_name & ': ' & head_commit.message}\"},
       \"http_delivery\":{\"version\":1,\"method\":\"POST\",\"path\":\"chat.postMessage\",
         \"headers\":{},\"body\":\"json\"},
       \"http_success\":{\"evaluator\":\"json_boolean\",\"field\":\"ok\",\"expected\":true}}" | jq -r .id)
```

A Source and a Subscription are both created Inactive, so they can be reviewed before anything
flows. Activate both once the GitHub webhook is configured:

```bash
curl -s -X POST "$ADMIN/admin/tenants/$TENANT/sources/$SOURCE/activate" -H "$AUTH" | jq .status
curl -s -X POST "$ADMIN/admin/tenants/$TENANT/topics/$TOPIC/subscriptions/$SUBSCRIPTION/activate" -H "$AUTH" | jq .status
```

Adjust the transform's hardcoded `#deploys` channel, or extend it to read a channel per repository,
before relying on this in a real workspace.

## 8. Push and observe

A TenantApiKey is required to inspect an Event through Ingestion's authenticated `/events/{id}` endpoint,
even though GitHub itself never presents one — GitHub authenticates through source verification,
not TenantApiKey. Create one now so you can look up the Event this webhook produces:

```bash
TOKEN=$(curl -s -X POST "$ADMIN/admin/tenants/$TENANT/tenant-api-keys" -H "$AUTH" \
  -H 'Content-Type: application/json' -d '{"name":"acme-inspect"}' | jq -r .token)
```

Push a commit to the repository. GitHub delivers the webhook to `callback_url`; Ingestion verifies
the signature, derives `github.push` as the Event type, and durably accepts it before responding.
Worker fans it out to the `push-to-slack` Subscription, transforms the payload, and delivers it to
Slack. There is no list-recent-Events endpoint, so pull the accepted `event_id` from the Ingestion
acceptance log line:

```bash
EVENT=$(docker compose logs ingestion --no-color | grep -oE 'Accepted webhook event [0-9a-f-]+' | tail -1 | awk '{print $NF}')
curl -s "$INGESTION/events/$EVENT" -H "Authorization: Bearer $TOKEN" | jq
```

A `delivery_attempts[].status` of `succeeded` with `response_status_code: 200` means Slack accepted
and confirmed the message logically (`ok: true`); a `dead_lettered` EventDelivery despite an
HTTP 200 attempt means Slack returned `ok: false`, which the Subscription's `json_boolean`
success rule classifies as a terminal delivery failure rather than a false success.

## Recovery notes

- **GitHub delivery failures**: Integrios does not control GitHub's redelivery. If Ingestion is
  unreachable or the deployment restarts mid-request, use GitHub's own **Webhooks → Recent
  Deliveries → Redeliver** to resend the exact same request; endpoint-scoped deduplication on
  `X-GitHub-Delivery` means a redelivered request that already succeeded is accepted as a duplicate,
  not processed twice.
- **Rotating the shared GitHub secret**: source-verification rotation is Operator-coordinated, not
  zero-downtime. Update `./secrets/sources/SourceSecrets__acme__github-webhook-secret`, restart
  Ingestion, then immediately update the same value in GitHub's webhook settings during a quiet
  period; requests in the gap between the two updates fail verification and need manual
  redelivery afterward.
- **Rotating the Slack bot token**: update `./secrets/destinations/DestinationSecrets__acme__slack-bot-token`
  and restart the Worker. Pending retries and replays after the restart use the new value, with
  no coordinated cutover required.
