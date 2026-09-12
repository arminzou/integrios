# Connector manifest examples

Four working Connector manifests, applied as ordinary Operator input. Every property they use, and
every rule they are validated against, is documented in the
[Connector manifest reference](../../docs/connector-manifest.md).

| File | Key | Direction | What it shows |
|---|---|---|---|
| `http.json` | `http` | both | The generic Connector: Event API input and HTTP delivery, offering `api_key_header` and `bearer_token` while still permitting unauthenticated delivery. |
| `github.json` | `github` | source | A verified provider webhook: `hmac_sha256` required, with `allow_unverified` set to `false`, and the Event types GitHub produces. |
| `slack.json` | `slack` | destination | A delivery-only Connector that requires `bearer_token`, with a configuration schema that admits `base_uri` and nothing else. |
| `dataverse.json` | `dataverse` | source | A source-only Connector that constrains nothing: an empty configuration schema, and unverified input permitted. |

Each is applied at its own key and contract version, for example:

[docs/setup.md](../../docs/setup.md) applies `http.json` on the way to a first delivered Event, and
[docs/github-to-slack-walkthrough.md](../../docs/github-to-slack-walkthrough.md) applies
`github.json` and `slack.json` end to end.
