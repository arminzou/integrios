# Connector manifest reference

A Connector is a deployment-wide capability definition, and its manifest is the JSON document that
states what the Connector can do and what an Operator may configure on it. A manifest never names a
Tenant, a Topic, or a single integration.

Every property is listed below. Validation is strict in both directions: a property the platform
does not know is rejected by name at every level of the document, and a declared capability the
platform cannot honour is rejected rather than stored and ignored. A typo fails loudly; it is never
silently dropped.

Working manifests live in `examples/connectors/`.

## Top-level properties

| Property | Required | Type | Rules |
|---|---|---|---|
| `manifest_schema_version` | yes | integer | Must be `1`. Versions this manifest format, not your Connector. |
| `key` | yes | string | Lower snake_case starting with a letter: `^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$`. The stable identity that Tenant resources refer to. |
| `contract_version` | yes | integer | Positive. Your Connector's own version. A published version's functional contract is fixed, so a changed contract is a new `contract_version`. |
| `direction` | yes | string | `source`, `destination`, or `both`. Decides which other properties are valid. |
| `source_configuration_schema` | source-capable only | object | [Constrained JSON Schema](#configuration-schemas) for what an Operator configures on a Source. Required when `direction` is `source` or `both`; rejected otherwise. |
| `destination_configuration_schema` | destination-capable only | object | The same, for a Destination. Required when `direction` is `destination` or `both`; rejected otherwise. |
| `source_verification` | yes | object | How inbound Events may be proven authentic. See [Verification and authentication](#verification-and-authentication). |
| `destination_authentication` | yes | object | How outbound Deliveries may authenticate. Same section. |
| `presentation` | yes | object | What an Operator reads when choosing this Connector. See [Presentation](#presentation). |

`source_verification` and `destination_authentication` are both required whatever the `direction`.
A `source`-only Connector still states that it permits no destination authentication rather than
leaving the question open.

## Configuration schemas

A Connector's manifest is authored once, when the Connector is installed. The configuration it
describes is written later and by someone else: whoever authors a Source or a Destination in a
Tenant, who submits a free-form JSON object. These schemas are how the manifest states, in advance,
what that object may contain — and the platform holds every authoring action to it.

The check runs when a Source or Destination is created and when it is updated, and again when a
Subscription routes to a Destination, so a resource cannot reach use against a contract it fails.
A required field that is absent, a field the schema does not declare while `additionalProperties`
is `false`, a value of the wrong type, outside an `enum`, or past a length or numeric bound is
refused with a message naming the field. A Connector capable of a direction but declaring no schema
for it is refused too: the question has to be answered, even if the answer is "anything", which is
what an empty `properties` with `additionalProperties: true` says.

One field is not the Connector's to constrain. A Destination's `base_uri` must be an absolute HTTP
or HTTPS URI with no query string or fragment whatever the schema declares, because delivery reads
that value out of the stored configuration.

### What a schema decides, in practice

`examples/connectors/slack.json` declares this:

```json
"destination_configuration_schema": {
  "type": "object",
  "properties": { "base_uri": { "type": "string", "format": "uri" } },
  "required": ["base_uri"],
  "additionalProperties": false
}
```

A Tenant then authors a Destination from that Connector, with
`POST /admin/tenants/{tenantId}/destinations`. The schema above decides which of these bodies is
stored and which is refused:

```jsonc
// Accepted.
{ "connector_id": "…", "name": "team-alerts",
  "configuration": { "base_uri": "https://hooks.slack.com/services/T/B/X" } }

// Destination configuration field 'base_uri' is required.
{ "connector_id": "…", "name": "team-alerts", "configuration": {} }

// Destination configuration field 'channel' is not allowed.
{ "connector_id": "…", "name": "team-alerts",
  "configuration": { "base_uri": "https://hooks.slack.com/services/T/B/X", "channel": "#ops" } }

// Destination configuration field 'base_uri' must be a string.
{ "connector_id": "…", "name": "team-alerts", "configuration": { "base_uri": 42 } }
```

The third is `additionalProperties: false` earning its place: the manifest says this Connector reads
`base_uri` and nothing else, so a field an Operator imagined — or mistyped — fails while they are
authoring, rather than being stored and never read.

`examples/connectors/github.json` takes the opposite position for its Source: `properties` is empty
and `additionalProperties` is `true`, so every configuration is accepted. That is a decision the
manifest states, not an omission.

`source_configuration_schema` and `destination_configuration_schema` are JSON Schema restricted to a
subset the platform can enforce. The root must be `"type": "object"` and must carry a `properties`
object. Nested object schemas are not supported, so a configuration is one flat level of typed
fields.

Allowed keywords, by the `type` of the node:

| `type` | Keywords |
|---|---|
| `object` (root only) | `type`, `properties`, `required`, `additionalProperties` |
| `string` | `type`, `enum`, `format`, `minLength`, `maxLength` |
| `number`, `integer` | `type`, `enum`, `minimum`, `maximum` |
| `boolean` | `type`, `enum` |

`type` is required on every node. Any other keyword — `$ref`, `oneOf`, `pattern`, `items`,
`default`, `description` — is rejected by name, as is any `type` outside the four above.

| Keyword | Rules |
|---|---|
| `properties` | Required on the root. Property names cannot be empty. |
| `required` | Unique names, each declared in `properties`. |
| `additionalProperties` | Boolean. |
| `format` | `uri` or `hostname`. |
| `minLength`, `maxLength` | Non-negative integers; `minLength` cannot exceed `maxLength`. |
| `minimum`, `maximum` | Numbers, and whole numbers under `"type": "integer"`; `minimum` cannot exceed `maximum`. |
| `enum` | Non-empty array of unique values matching the node's type. Under `"type": "integer"`, only integers. |

A configuration schema describes what the Connector's own integration reads — an endpoint, a
workspace, a payload format. Platform behaviour is not configurable from here: the retry budget, for
example, is the deployment's `Integrios:Delivery:Retry:MaxAttempts`, and a manifest field named
after it would be accepted as an ordinary field and then read by nothing.

The keywords in use, with illustrative fields:

```json
"destination_configuration_schema": {
  "type": "object",
  "properties": {
    "base_uri": { "type": "string", "format": "uri" },
    "workspace": { "type": "string", "minLength": 1, "maxLength": 64 },
    "content_format": { "type": "string", "enum": ["json", "form"] }
  },
  "required": ["base_uri"],
  "additionalProperties": false
}
```

A Connector that takes no configuration still declares the schema, with an empty `properties`
object.

## Verification and authentication

Both objects take exactly two properties, and both are required. The array may be empty; it may not
be absent.

| Object | Properties |
|---|---|
| `source_verification` | `allow_unverified` (boolean, required), `schemes` (array, required) |
| `destination_authentication` | `allow_unauthenticated` (boolean, required), `schemes` (array, required) |

Each entry in `schemes` is an object carrying all three of these, empty arrays included:

| Property | Type | Rules |
|---|---|---|
| `scheme` | string | Lower snake_case, unique within its array. |
| `required_config` | array of string | Unique lower snake_case field names. |
| `required_secret_refs` | array of string | Unique lower snake_case field names. |

Rules across the two objects:

- A source-capable Connector must either declare a verification scheme or set `allow_unverified` to
  `true`; a destination-capable one must either declare a scheme or set `allow_unauthenticated`.
  Silence is not a default — the manifest has to say which it means.
- Schemes may only be declared for a direction the Connector has. A `source` Connector carrying
  destination authentication schemes is rejected.

**Schemes are platform contracts, not free text.** A declared scheme must name one the platform
implements *and* list exactly the fields that implementation requires — no more, no fewer. A
manifest declares which of the platform's schemes the Connector permits; it cannot define one.

| Declared in | `scheme` | `required_config` | `required_secret_refs` |
|---|---|---|---|
| `source_verification` | `hmac_sha256` | *(empty)* | `secret` |
| `destination_authentication` | `api_key_header` | `header_name` | `api_key` |
| `destination_authentication` | `bearer_token` | *(empty)* | `token` |

## Presentation

| Property | Required | Type | Rules |
|---|---|---|---|
| `name` | yes | string | Non-empty. What an Operator sees when choosing a Connector. |
| `description` | no | string | Free text. |
| `event_types` | yes | array of string | May be empty. Values are non-empty and unique. The Event types this Connector is expected to produce. |
| `authoring_presets` | yes | array of object | May be empty. Objects only. |

`event_types` and `authoring_presets` are required as properties even when empty. Both are preserved
verbatim, and the dashboard's guided Connector form keeps them across an import and re-apply.
Nothing in the platform's routing or delivery behaviour reads them today: they record intent and
feed authoring aids.

`presentation` is the one part of a published `contract_version` that may be revised in place. A
better name or description changes nothing an existing Source or Destination depends on, while any
functional change does.

## A complete manifest

`examples/connectors/http.json`, the generic HTTP Connector, in full:

```json
{
  "manifest_schema_version": 1,
  "key": "http",
  "contract_version": 1,
  "direction": "both",
  "source_configuration_schema": {
    "type": "object",
    "properties": {},
    "additionalProperties": true
  },
  "destination_configuration_schema": {
    "type": "object",
    "properties": {
      "base_uri": { "type": "string", "format": "uri" }
    },
    "required": ["base_uri"],
    "additionalProperties": false
  },
  "source_verification": {
    "allow_unverified": true,
    "schemes": []
  },
  "destination_authentication": {
    "allow_unauthenticated": true,
    "schemes": [
      {
        "scheme": "api_key_header",
        "required_config": ["header_name"],
        "required_secret_refs": ["api_key"]
      },
      {
        "scheme": "bearer_token",
        "required_config": [],
        "required_secret_refs": ["token"]
      }
    ]
  },
  "presentation": {
    "name": "HTTP",
    "description": "Generic Event API input and HTTP delivery using the platform's ordinary source and destination capabilities.",
    "event_types": [],
    "authoring_presets": []
  }
}
```
