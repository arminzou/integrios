# Walkthrough: GitHub to Slack

Drive a verified GitHub webhook through Integrios and deliver a transformed Slack message,
entirely through the Admin and Ingestion APIs. This exercises the platform's generic
verified-webhook source adapter, Operator-authored Connectors, and logical HTTP-outcome
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
catalog).

## 1. Apply the example Connector manifests

The [`examples/connectors/`](../examples/connectors/) directory carries the exact
machine-validated manifests this walkthrough uses. `github-v1.json` selects the authoring-safe
`verified_webhook` v1 source adapter; `slack-v1.json` is generic HTTP with bearer-token
authentication and a `json_boolean` success rule, because Slack's `chat.postMessage` can return
HTTP 200 for a request it rejected. Apply is idempotent — a missing version is created, and
re-applying the identical manifest is a no-op.

Apply returns the created Connector, including its deployment-wide `id`, which every Connection
against it references below.

```bash
ADMIN=http://localhost:5150
AUTH="Authorization: OperatorKey global_operator_key:operator_bootstrap_secret"

GITHUB_CONNECTOR=$(curl -s -X PUT "$ADMIN/admin/connectors/github/versions/1" -H "$AUTH" \
  -H 'Content-Type: application/json' --data-binary @examples/connectors/github-v1.json | jq -r .id)

SLACK_CONNECTOR=$(curl -s -X PUT "$ADMIN/admin/connectors/slack/versions/1" -H "$AUTH" \
  -H 'Content-Type: application/json' --data-binary @examples/connectors/slack-v1.json | jq -r .id)
```

Bootstrap installs neither: only the deployment-wide generic `http` Connector is built in. These
two are ordinary Operator-authored Connectors, validated by the same manifest parser and
authoring rules any Operator-authored manifest goes through.

## 2. Create a Tenant

```bash
TENANT=$(curl -s -X POST "$ADMIN/admin/tenants" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"slug":"acme","name":"Acme","environment":"production"}' | jq -r .id)
```

## 3. Create the GitHub source Connection

The Connection's `source_verification` selects the `hmac_sha256` scheme and names one secret
reference; it does not carry the secret value itself. Every source endpoint later created against
this Connection shares this same verification secret — a different GitHub signing secret requires a
different Connection.

```bash
GITHUB_CONN=$(curl -s -X POST "$ADMIN/admin/tenants/$TENANT/connections" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"connector_id\":\"$GITHUB_CONNECTOR\",\"name\":\"acme-github\",\"config\":{},
       \"source_verification\":{\"scheme\":\"hmac_sha256\",\"config\":{},
         \"secret_refs\":{\"secret\":\"github_webhook_secret\"}},
       \"environment\":\"production\"}" | jq -r .id)
```

Materialize the shared secret value the default file-based secret provider expects — generate one
and keep it, GitHub needs the same value in step 6:

```bash
GITHUB_SECRET=$(openssl rand -hex 32)
mkdir -p ./secrets/source/acme
printf '%s' "$GITHUB_SECRET" > ./secrets/source/acme/github_webhook_secret
```

(See [setup.md](setup.md#destination-authentication-secrets) for the mirrored destination-side
convention and the `configuration` provider alternative; source-verification secrets follow the
exact same shape under `secrets/source/` instead of `secrets/destination/`.)

## 4. Create a Topic and get the callback URL

Creating the Topic with this Connection as a source mints a stable source endpoint, because the
GitHub Connector's manifest declares a `source_contracts` entry. Removing and re-adding the association
later would mint a new endpoint identity; this one is stable for as long as the association exists.

```bash
TOPIC=$(curl -s -X POST "$ADMIN/admin/tenants/$TENANT/topics" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"github-events\",\"source_connection_ids\":[\"$GITHUB_CONN\"]}" | jq -r .id)

CALLBACK_URL=$(curl -s "$ADMIN/admin/tenants/$TENANT/topics/$TOPIC" -H "$AUTH" \
  | jq -r '.sources[0].endpoint.callback_url')
echo "$CALLBACK_URL"
```

`callback_url` is derived from the deployment's configured `Integrios__PublicIngestionBaseUri`; see
[Prerequisites](#prerequisites) if this doesn't already point at a GitHub-reachable HTTPS origin.

## 5. Configure the GitHub webhook (manual, external)

In the GitHub repository or organization's **Settings → Webhooks → Add webhook**:

- **Payload URL**: the `callback_url` from step 4.
- **Content type**: `application/json` — the verified-webhook adapter and this example's
  `event_type_header`/`event_type_action_field` configuration both assume JSON; GitHub's
  form-encoded content type is not accepted.
- **Secret**: the exact value of `$GITHUB_SECRET` from step 3.
- **Events**: at minimum, "Just the push event" is enough to exercise this walkthrough end to end.

GitHub sends a `ping` request immediately after you save the webhook. Integrios accepts it as an
ordinary `github.ping` Event — there is nothing special to check for it, and it will appear in the
Event history alongside real pushes.

## 6. Create the Slack destination Connection

`base_uri` is Slack's API base; the Subscription (step 8) supplies the relative
`chat.postMessage` path.

```bash
SLACK_CONN=$(curl -s -X POST "$ADMIN/admin/tenants/$TENANT/connections" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"connector_id\":\"$SLACK_CONNECTOR\",\"name\":\"acme-slack\",
       \"config\":{\"base_uri\":\"https://slack.com/api\"},
       \"destination_authentication\":{\"scheme\":\"bearer_token\",\"config\":{},
         \"secret_refs\":{\"token\":\"slack_bot_token\"}},
       \"environment\":\"production\"}" | jq -r .id)

mkdir -p ./secrets/destination/acme
printf '%s' 'xoxb-REPLACE-WITH-YOUR-BOT-TOKEN' > ./secrets/destination/acme/slack_bot_token
```

A leading or trailing newline in this file (for example from re-saving it in an editor with "insert
final newline" on) is trimmed automatically — it's never a legitimate byte of the secret, so the
platform strips it rather than failing. A line break *inside* the value is different: that's
genuine corruption and still fails closed, surfacing as a `request_construction` delivery failure
with `Auth secret field 'token' contains a line break`.

## 7. Subscribe GitHub pushes to the Slack Connection

The transform is a JSONata expression evaluated with the Event payload as its root and platform
metadata bound to `$context`; `http_delivery` supplies the method, relative path, and body shape,
all owned by the Subscription rather than the Connection.

```bash
curl -s -X POST "$ADMIN/admin/tenants/$TENANT/topics/$TOPIC/subscriptions" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"push-to-slack\",
       \"match_rules\":{\"event_type\":\"github.push\"},
       \"destination_connection_id\":\"$SLACK_CONN\",
       \"order_index\":0,
       \"transform\":{\"engine\":\"jsonata\",\"version\":\"1\",
         \"expression\":\"{'channel': '#deploys', 'text': pusher.name & ' pushed to ' & repository.full_name & ': ' & head_commit.message}\"},
       \"http_delivery\":{\"version\":1,\"method\":\"POST\",\"path\":\"chat.postMessage\",
         \"headers\":{},\"body\":\"json\"}}" | jq
```

Adjust the transform's hardcoded `#deploys` channel, or extend it to read a channel per repository,
before relying on this in a real workspace.

## 8. Push and observe

An TenantApiKey is required to inspect an Event through Ingestion's authenticated `/events/{id}` endpoint,
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
curl -s "http://localhost:5231/events/$EVENT" -H "Authorization: TenantApiKey $TOKEN" | jq
```

A `delivery_attempts[].status` of `succeeded` with `response_status_code: 200` means Slack accepted
and confirmed the message logically (`ok: true`); a `dead_lettered` SubscriptionDelivery despite an
HTTP 200 attempt means Slack returned `ok: false`, which the `json_boolean` success rule in
`slack-v1.json` classifies as a terminal delivery failure rather than a false success.

## Recovery notes

- **GitHub delivery failures**: Integrios does not control GitHub's redelivery. If Ingestion is
  unreachable or the deployment restarts mid-request, use GitHub's own **Webhooks → Recent
  Deliveries → Redeliver** to resend the exact same request; endpoint-scoped deduplication on
  `X-GitHub-Delivery` means a redelivered request that already succeeded is accepted as a duplicate,
  not processed twice.
- **Rotating the shared GitHub secret**: source-verification rotation is Operator-coordinated, not
  zero-downtime. Update the value in `./secrets/source/acme/github_webhook_secret`, then
  immediately update the same value in GitHub's webhook settings during a quiet period; requests
  in the gap between the two updates fail verification and need manual redelivery afterward.
- **Rotating the Slack bot token**: update `./secrets/destination/acme/slack_bot_token`; the Worker
  resolves the current value on every attempt, so in-flight retries pick up the new value
  automatically with no coordinated cutover required.
