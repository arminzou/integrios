# Architecture

## Product boundary

Integrios is an open-source, self-hostable, Operator-run, multi-tenant HTTP integration platform.
The engineering team running a deployment owns all control-plane configuration. A Tenant is an
ownership and isolation boundary for configuration and runtime data; it is not a control-plane user.

Integrios concentrates on one operational problem: durably accept an Event, apply Tenant-aware
routing and transformation, and reliably deliver it over HTTP while preserving retry, dead-letter,
replay, and attempt history. It is not a no-code workflow builder, connector marketplace, ETL
platform, API gateway, or multi-protocol runtime.

## System shape

![Integrios system shape: Operator configures Admin; an external Event producer, a provider webhook, or Azure Service Bus feed Ingestion; Admin and Ingestion share one PostgreSQL or SQL Server database; Worker reads fanout work from it, delivers over HTTP, and writes attempt state back](assets/architecture-system-shape.svg)

An Operator configures Admin, the control plane. Three intake paths feed Ingestion, the data plane:
an external Event producer posts through an `event_api` Source with a TenantApiKey, a provider's
HTTP request arrives through a `webhook` Source, or a message is received through a `queue` Source
backed by Azure Service Bus. Admin and Ingestion share one PostgreSQL or SQL Server 2022+ database:
Admin writes configuration to it, and Ingestion writes each accepted Event and its outbox row to it
in one transaction. Worker reads fanout work from that same database, delivers over generic HTTP to
Tenant-owned destinations, and writes attempt state, retries, and dead-letters back to it.

A **Source** (`event_api`, `webhook`, or `queue`) is the Operator-authored resource that binds one
Connection to one Topic and authorizes it to publish there. Generic Event intake through the
`event_api` Source type, addressed by Source id and a TenantApiKey, remains the universal path. A
`webhook` Source additionally lets a Connector's manifest verify and normalize a provider's HTTP
request before it crosses the same durable Event-acceptance boundary; see [the GitHub-to-Slack
walkthrough](github-to-slack-walkthrough.md) for a concrete, currently-shipped example. A `queue`
Source receives messages from an existing Azure Service Bus queue or topic subscription instead of
accepting inbound HTTP requests.

## Control plane and data plane

Integrios separates platform intent from runtime execution.

**Control plane** (`Integrios.Admin`): Operator-owned Tenant lifecycle, Connector authoring,
Connection configuration and secret references, Topic and Subscription authoring, and transform
preview. Tenants never receive control-plane authority.

**Data plane** (`Integrios.Ingestion` and `Integrios.Worker`): request authentication and Tenant
resolution, source-Connection and Topic validation, durable Event acceptance, fanout, transformation,
HTTP delivery, retries, dead-lettering, replay, and delivery tracking.

The services share the configured PostgreSQL or SQL Server 2022+ database. Admin owns configuration
writes; Worker reads the configuration it needs directly from that database and does not call Admin
at runtime.

## Core model

- **Tenant** is the top-level ownership and isolation boundary.
- **TenantApiKey** is an Integrios-issued machine credential for generic Event intake and resolves one
  Tenant.
- **Connector** is a deployment-wide reusable declarative HTTP contract for an external-system
  class. It is explicitly applied by the Operator, shared across Tenants, and contains no Tenant
  data or executable code.
- **Connection** is a Tenant-owned configured instance of one Connector. It owns Tenant-specific
  endpoint configuration plus separate source-verification and destination-authentication
  selections and secret references. A Connection is not itself a source or a destination; a
  **Source** or a Subscription's `destination_connection_id` gives it that role.
- **Source** is a Tenant-owned resource, persisted independently of both the Connection and the
  Topic it binds together, that authorizes one Connection to publish into one Topic. Its `type`
  (`event_api`, `webhook`, or `queue`) selects the intake mechanism, and its `configuration` names
  the Connector-declared `source_contract` it uses (plus, for a `webhook` Source, a
  platform-generated `callback_id`).
- **Topic** is a Tenant-owned named Event stream. Configured Sources may publish to it.
- **Subscription** independently filters a Topic, optionally transforms matching Events, and
  delivers them through one destination Connection. It owns the versioned HTTP delivery
  configuration and its own delivery/DLQ scope.
- **Event** is the accepted durable work item from one Source on one Topic.
- **EventDelivery** is the per-(Event, Subscription) state and execution snapshot created by
  fanout.
- **DeliveryAttempt** records one concrete outbound execution.

The same Connector can back Connections for many Tenants. For example, one deployment-wide
`klaviyo` Connector can constrain separate Premier Group and Contoso Connections without sharing
their base URIs, credentials, or runtime data.

## Source model

A Source is created through Admin (`POST /admin/tenants/{id}/sources`) with a `connection_id`, a
`topic_id`, a `type`, and a `configuration` object whose allowed keys depend on `type`. Every
`configuration` names a `source_contract` — one of the Connector's declared source contracts — that
governs how the raw input is validated and, optionally, mapped to the Event's `event_type`,
`payload`, `source_event_id`, and `metadata`. With no mapping declared, the caller's raw input is
the Event output directly, strictly bounded to those four fields.

**`event_api`** is the universal, generic path. An external **Event producer** — an
Operator-controlled application or automation such as a source-system plugin, Power Automate flow,
or small service — owns source-system credentials and provider-specific adaptation, authenticates to
Ingestion with a TenantApiKey, and posts to `POST /events?source_id={id}`.

**`webhook`** lets an Operator-authored Connector manifest opt a provider's HTTP intake into the same
durable Event-acceptance boundary without adding or rebuilding Integrios code. The manifest supplies
signature header, encoding, delivery-identity header, and Event-type-derivation header as data; the
platform verifies HMAC-SHA256 over the exact raw request body before parsing, derives a
provider-qualified Event type (for example `github.issues.opened`) from that data, and retains the
JSON payload unchanged. Creating a `webhook` Source generates a `callback_id`; the public intake
path is `POST {IngestionBaseUri}/webhooks/{callback_id}`. The `github.json` example under
[`examples/connectors/`](../examples/connectors/) is a real, machine-validated instance of this, not
a hypothetical. Providers that do not fit this closed shape use an external Event API client unless
repeated demand justifies another provider-neutral platform capability.

**`queue`** receives messages from an existing Azure Service Bus queue or topic subscription instead
of accepting inbound HTTP requests. Its `configuration` additionally names the transport
(`azure_service_bus`), the namespace and entity to read from, and an authentication scheme
(`connection_string` with a secret reference, or `azure_identity` for an ambient credential).
Ingestion runs one background processor per active `queue` Source, reconciled on an interval so
authoring changes take effect without a restart; a deployment with no `queue` Source configured
creates no Service Bus client and needs no Azure credentials. Integrios never provisions the
namespace, queue, topic, or subscription itself — the Operator points a Source at an entity that
already exists.

Operator-authored Connectors cannot load runtime code — `webhook` verification and `queue` receiving
are entirely platform-owned, and a manifest or Source `configuration` only supplies bounded data for
them. Polling remains external Event-producer behavior. Integrios does not commit to a broad
provider set or an in-process plugin system.

## Destination model

HTTP(S) is the only destination protocol. One generic HTTP module executes every outbound request;
there are no provider-specific destination execution paths or destination-action domain objects.

- a destination Connection owns the absolute base URI, authentication, Tenant-specific non-secret
  configuration, and secret references
- a Subscription owns a versioned method (`POST`, `PUT`, `PATCH`, or `DELETE`), a literal relative
  path, restricted static headers, and a transformed JSON body or explicit no-body
- the relative path always appends to the Connection's base path with one normalized boundary
  slash; it can never replace the Connection's scheme, host, port, or base path
- fanout snapshots the HTTP request shape, relevant non-secret Connection configuration, secret
  references, and the Connector's effective HTTP success rule together, so a later edit
  cannot change an in-flight delivery's request or success criteria; the Worker resolves current
  secret values for each attempt
- a Connector may declare an optional HTTP success rule: the default `status_code` evaluator
  treats any `2xx` response as success, while a `json_boolean` evaluator additionally asserts that a
  configured top-level response field equals an expected boolean, so a provider that returns `2xx`
  for an operation it actually rejected (Slack's `chat.postMessage` is the shipped example) is
  correctly classified as a failure
- failure disposition is fixed platform policy: transport errors, timeouts, and HTTP
  408/429/5xx retry with backoff to exhaustion; every other outcome, including a logically-rejected
  `2xx`, dead-letters immediately for Operator replay; a bounded `Retry-After` on 429/503 is honored
  over the computed backoff when present
- successful response bodies do not become persisted workflow state or new Events; a response body
  is only ever read (bounded) when the success rule requires it

Dynamic headers, arbitrary methods, `GET`/`HEAD`/`CONNECT`/`TRACE`, form or multipart data, binary or
streaming bodies, response-driven workflows, OAuth 2.0 client credentials, and non-HTTP protocols
remain out of scope. Updating several external entities means creating several independent
Subscriptions so each update retains its own retry, DLQ, and replay lifecycle.

## Core processing flow

1. An external Event producer sends the Event contract to `POST /events?source_id={id}` and
   authenticates with a TenantApiKey, a `webhook` Source verifies and normalizes a provider HTTP
   request at `POST /webhooks/{callback_id}`, or a `queue` Source's background processor receives a
   message from Azure Service Bus — before Ingestion ever sees an Event contract in any case.
2. Ingestion resolves the Tenant and the addressed Source, which names the active Connection and the
   Topic it may publish to.
3. One database transaction writes the canonical Event and its outbox row before Ingestion
   acknowledges acceptance.
4. Worker fanout reads matching active Subscriptions and creates one EventDelivery for each.
5. Each EventDelivery is claimed independently, transformed, and sent through the generic
   HTTP delivery module.
6. Worker records the DeliveryAttempt and advances that delivery to success, retry, or dead letter.
7. Replay schedules dead-lettered delivery work again without discarding prior attempt history.

## Durability and delivery semantics

### Durable acceptance and transactional outbox

The Event and outbox row are committed together. This prevents both “accepted without enqueueing”
and “enqueued without accepting.” The outbox decouples source response time from downstream
availability and avoids a database/message-transport dual write.

### Idempotency and source provenance

An `event_api` caller addresses a `source_id` and may provide a `source_event_id`. Ingestion accepts
the Source only when it belongs to the authenticated Tenant, is active, and may publish to its
Topic. There is no separate idempotency key field: when a `source_event_id` is supplied, the
idempotency key is `{source_id}:{source_event_id}`, and repeated submissions with the same key
resolve to the same accepted Event.

Provider credentials and webhook secrets are not Integrios TenantApiKeys. Connections store logical
secret references; the Operator materializes their values through the deployment's secret provider,
and Worker resolves them immediately before an attempt without persisting the values.

### Independent, at-least-once delivery

Every matching Subscription gets independent state, retry scheduling, dead-lettering, and replay.
One failing destination does not block another.

Outbound HTTP delivery is at least once. A process can stop after a destination accepts a request but
before Integrios records success, so recovery may repeat the logical delivery. Stable Event and
EventDelivery identifiers let downstream systems deduplicate; per-attempt identifiers support
diagnostics. Fenced leases prevent an older Worker from overwriting a newer authoritative result,
and indeterminate attempts preserve ambiguity rather than reporting a false confirmed failure.

## Scaling and observability

Ingestion instances are stateless and Worker replicas safely claim disjoint work with PostgreSQL
`FOR UPDATE SKIP LOCKED` or equivalent SQL Server locking hints. The configured database is the
durable backbone; another transport should be introduced only when measured scale or operational
pressure justifies it.

Integrios emits structured logs, metrics, and OTLP-capable traces but bundles no production
observability backend. Aggregate telemetry remains low-cardinality; Tenant-specific delivery detail
comes from the durable Event, EventDelivery, and DeliveryAttempt model.
