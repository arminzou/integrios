# Observability

Integrios emits Prometheus metrics, structured stdout logs, and OpenTelemetry traces. It bundles no production observability backend: Operators collect these signals with the systems they already run. Integrios provides no default alerts, thresholds, or SLOs.

Hosts share AddOperationalConsoleLogging and AddTelemetryServices. Admin and Ingestion add UseRequestCompletionLogging; the Worker adds AddOutboxDepthMetricsServices.

## Metrics

All three hosts expose `/health`, `/ready`, and Prometheus-format `/metrics` on an operational
listener. `/health` has no dependency checks. `/ready` checks only database connectivity and
returns 503 while the database is unavailable. Admin and Ingestion product listeners do not expose
these routes.

| Host | Local operational URL | In-container port |
|---|---|---|
| Ingestion | http://localhost:5232 | 5299 (`OperationalPort`) |
| Admin | http://localhost:5151 | 5299 (`OperationalPort`) |
| Worker | http://localhost:5299 | 5299 (`WorkerMetricsPort`) |

The operational listener is for probes and scraping within the deployment network. The local
Compose stack publishes the Admin and Ingestion operational ports for development; production
deployments keep them private. Alongside standard ASP.NET Core, HTTP client, and runtime
instruments, Integrios emits the following application instruments.

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| integrios_events_ingested_total | counter | — | Events accepted at the durable boundary; idempotent duplicates are excluded. |
| integrios_events_unrouted_total | counter | — | Events that matched no Subscription during fanout. |
| integrios_queue_source_errors_total | counter | transport | Queue Source processing errors, grouped by transport. |
| integrios_fanout_rows_created_total | counter | — | EventDelivery rows created by fanout. |
| integrios_deliveries_succeeded_total | counter | connector_key | Successful deliveries by destination Connector class. |
| integrios_deliveries_failed_total | counter | connector_key, http_status_class | Retryable delivery failures. |
| integrios_deliveries_dead_lettered_total | counter | connector_key | Deliveries that exhausted their retry budget. |
| integrios_delivery_secret_resolution_failures_total | counter | connector_key | Delivery attempts unable to resolve a required secret reference. |
| integrios_delivery_request_construction_failures_total | counter | connector_key | Delivery attempts unable to construct an outbound request. |
| integrios_delivery_stale_finalizations_total | counter | — | Finalization results discarded after delivery ownership was lost. |
| integrios_delivery_attempt_duration_seconds | histogram | result, connector_key | Outbound delivery-attempt duration. |
| integrios_outbox_pending_depth | gauge | — | All unprocessed outbox rows, including rows deferred until a future eligibility time. |
| integrios_outbox_oldest_pending_age_seconds | gauge | — | Age of the oldest unprocessed outbox row, measured from its eligibility time. |
| integrios_delivery_ready_depth | gauge | — | Delivery work claimable now, including recoverable expired leases and excluding future retries. |
| integrios_delivery_oldest_ready_age_seconds | gauge | — | Age of the oldest claimable Delivery, measured from its eligibility time. |
| integrios_backlog_snapshot_age_seconds | gauge | — | Time since the Worker last sampled backlog state successfully. |

http_status_class is one of 2xx, 4xx, 5xx, timeout, or error. All labels are platform-owned and bounded: transport, connector_key, http_status_class, and result. Tenant, Event, Subscription, Connection, and Delivery identifiers never appear in metric labels.

### Backlog gauges

The five backlog gauges are Worker-only and deployment-global. A Worker samples the database asynchronously every 15 seconds by default; configure a positive .NET TimeSpan with Integrios__Telemetry__OutboxDepthSampleInterval, for example 00:00:30. Scrapes read the cached snapshot and never query the database.

integrios_outbox_pending_depth and integrios_delivery_ready_depth are deliberately asymmetric: the former includes deferred outbox rows, while the latter includes only delivery work claimable now. They are not counterpart gauges. With multiple Worker replicas, use max, not sum, for these deployment-global gauges.

No backlog values are emitted before the first successful sample. If a later sample fails, the last values remain available and snapshot age increases.

Example PromQL queries below are starting points for an Operator's own alerting and SLO policy, not product thresholds:

    # Backlog is growing over the last 15 minutes.
    delta(integrios_outbox_pending_depth[15m])

    # Dead-letter increase by destination Connector class.
    sum by (connector_key) (increase(integrios_deliveries_dead_lettered_total[15m]))

    # Retryable delivery failure-to-success ratio by Connector class.
    sum by (connector_key) (rate(integrios_deliveries_failed_total[5m]))
    / sum by (connector_key) (rate(integrios_deliveries_succeeded_total[5m]))

    # P95 delivery-attempt duration.
    histogram_quantile(0.95, sum by (le, connector_key) (rate(integrios_delivery_attempt_duration_seconds_bucket[5m])))

    # Stale Worker backlog snapshot.
    max(integrios_backlog_snapshot_age_seconds)

## Traces

Integrios persists W3C trace context across Event acceptance, fanout, and Delivery retries, so an Event can remain correlated across asynchronous work. The stable lifecycle spans are:

- event.accept
- outbox.fanout
- delivery.attempt
- delivery.transform
- delivery.http
- delivery.finalize

Custom trace attributes use the integrios.* namespace. Processing and retry continue when a trace receiver is unavailable. The Admin Event detail response also exposes nullable trace_id: it is the persisted root trace ID when valid, otherwise null. Treat it as an opaque value for correlation with the tracing backend.

Each host supplies these resource attributes:

| Host | service.name |
|---|---|
| Admin | integrios-admin |
| Ingestion | integrios-ingestion |
| Worker | integrios-worker |

service.version is the built Integrios version and service.instance.id is a generated instance identifier. Add Operator resource attributes, such as deployment.environment.name, through standard OTEL_RESOURCE_ATTRIBUTES.

Root sampling uses standard OTEL_TRACES_SAMPLER and OTEL_TRACES_SAMPLER_ARG; the default is parentbased_always_on. Sampling decisions propagate with the persisted trace context.

### OTLP trace export

OTLP exports traces only. Metrics remain Prometheus-scraped and logs remain on stdout. Set OTEL_EXPORTER_OTLP_ENDPOINT to an absolute HTTP(S) endpoint to enable trace export:

    OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317

With no endpoint, traces are not exported. An invalid endpoint prevents host startup rather than silently using a fallback. There is no Integrios-specific OTLP endpoint setting.

## Logs

Packaged Admin, Ingestion, and Worker hosts write JSON logs to stdout. Development uses readable console output instead. Log scopes include operational identifiers such as event_id, delivery_id, and subscription_id; active traces also add TraceId and SpanId.

Admin and Ingestion emit one completion record for each non-operational HTTP request with method, route_template, status, duration_ms, and request_id, plus trace correlation when active. The completion record never includes request bodies, query strings, arbitrary headers, or full URLs. It does not run for /health, /ready, /metrics, /_framework, /_content, /assets, or /favicon.ico. The Worker has only its operational HTTP surface and emits no completion record.

## Local Prometheus and Grafana

Prometheus and Grafana are optional local-development services. Start them with:

    make up-observability
    # or
    docker compose --profile observability up --build -d

Ordinary make up or docker compose up does not start them. When enabled, Prometheus is available at http://localhost:9090 and Grafana at http://localhost:3000. They are development convenience services, not a production backend.
