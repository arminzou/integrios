# Architecture

![Integrios architecture](assets/architecture-diagram.png)

## Why this design

Webhook-heavy and integration-heavy systems tend to fail in predictable ways:

- upstream systems retry when they do not get a timely acknowledgment
- downstream systems become slow, unavailable, or rate-limited
- naive request/response coupling turns transient failures into data loss or duplicated side effects

Integrios is built around well-established patterns that address those problems directly:

- durable acceptance boundaries to avoid data loss at ingress
- a transactional outbox to safely bridge write paths and async processing
- idempotent ingestion and at-least-once delivery semantics
- explicit retry, dead-letter, and replay paths for failure recovery
- strict tenant isolation and auditable delivery history

## Control plane vs data plane

Integrios uses a deliberate split between platform intent and runtime execution.

**Control plane** (`Integrios.Admin`): tenant lifecycle and boundaries, integration definitions and capability contracts, tenant connection configuration and secret references, topic and subscription configuration, and policy concerns like quotas, limits, and governance.

**Data plane** (`Integrios.Ingress` for intake, `Integrios.Worker` for delivery): ingress request validation, auth, and tenant resolution; durable acceptance-boundary persistence; outbox-driven asynchronous handoff; routing, transformation, and delivery execution against destination connections; retries, dead-lettering, replay, and delivery tracking; plus tracing, logging, and operational observability.

This separation keeps runtime processing paths focused while letting control logic evolve independently.

## Core processing flow

1. Ingest a webhook or API event.
2. Validate the request and resolve tenant context.
3. Authenticate the tenant-scoped API key.
4. Persist accepted work at the durable acceptance boundary.
5. Publish through the database + outbox path.
6. Fan out to matching subscriptions using tenant topic and subscription configuration.
7. Transform payloads per subscription rules.
8. Deliver to destination connections.
9. Track event status and delivery-attempt history.
10. Retry, dead-letter, or replay when recovery is needed.

## Key platform concepts

### Durable acceptance boundary

`Integrios.Ingress` accepts events and persists them durably before acknowledging the caller. The boundary is a single transaction that writes both:

- the canonical `events` record
- a corresponding `outbox` record for async processing

This guarantees the system never "accepts without enqueueing" or "enqueues without accepting."

### Transactional outbox

The outbox is the handoff between synchronous API requests and asynchronous worker execution. It avoids dual-write consistency bugs and lets the worker poll/claim work without coupling upstream latency to downstream reliability.

### Idempotency and de-duplication

Callers can provide an `idempotencyKey` on `POST /events`. Within a tenant scope, duplicate submissions with the same key resolve to the same accepted event, preventing duplicate downstream side effects from retries, network timeouts, or webhook replays.

### Multi-tenant isolation

Tenants are first-class boundaries in both auth and data access. API keys resolve to a tenant context, and event reads and writes are tenant-scoped, preventing cross-tenant exposure and enabling per-tenant operational controls.

### Asynchronous processing and backpressure

Worker execution is decoupled from ingestion, so the intake API stays responsive under load or downstream instability. This allows:

- smoothing bursty traffic
- isolating slow or failing downstream connections
- scaling workers independently from intake instances

### Reliability and failure resilience

The platform is built for controlled failure handling:

- retry policies with bounded attempts
- dead-letter queues for terminal failures
- replay paths for safe reprocessing
- delivery-attempt history for diagnostics and auditability

### Scalability model

Integrios scales horizontally, and *differently per deployment*, through configuration and preserved seams rather than bundled infrastructure:

- stateless intake instances behind load balancers
- worker concurrency tuned by queue depth and throughput targets
- tenant-aware routing and processing partitioning
- storage-backed durability with clear ownership of consistency boundaries
- transport behind a port (`IEventBus`): a Postgres outbox/bus by default, with an alternative like Kafka swappable in only when scale justifies it
- observability split into an operator plane (aggregate metrics) and a tenant plane (per-event drill-down from the durable model), exported to whatever backend the operating team runs

This supports progressive evolution from single-node operation to larger multi-instance deployments without forking the product for a given team's scale.
