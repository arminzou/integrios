# Integrios

[![CI](https://github.com/arminzou/integrios/actions/workflows/ci.yml/badge.svg)](https://github.com/arminzou/integrios/actions/workflows/ci.yml)

Integrios is an open-source, self-hostable, Operator-run HTTP integration platform. It gives
engineering teams a durable boundary for accepting events, Tenant-aware routing and transformation,
and reliable HTTP delivery with retries, dead-lettering, replay, and auditable delivery history.

## Features

- Durable event intake behind a transactional-outbox acceptance boundary, so no accepted event is lost
- TenantApiKey-authenticated generic intake with Tenant isolation
- Topic/subscription routing with optional JSONata payload transforms
- Authenticated HTTP JSON delivery using open, API-key-header, bearer-token, or OAuth 2.0 client-credentials authentication
- Reliable async delivery with bounded retries, dead-lettering, and replay
- Pull delivery (preview): Subscriber services claim deliveries over HTTP from the Delivery API, with the
  [Integrios.Client](https://www.nuget.org/packages/Integrios.Client) SDK on nuget.org
- Per-event status and delivery-attempt history
- Vendor-neutral observability: Prometheus metrics, structured stdout logs, and OpenTelemetry traces over OTLP; bring your own backend
- Clean control-plane / data-plane / worker separation; scales horizontally
- Self-hostable and Docker-first

## Architecture

Integrios splits platform intent from runtime execution. The **control plane** (`Integrios.Admin`) owns tenants, connectors, sources, destinations, topics, and subscriptions. The **data plane** takes over at runtime: `Integrios.Ingestion` validates, authenticates, and durably accepts events behind a transactional outbox; `Integrios.Worker` fans out to subscriptions, applies transforms, delivers to destinations, and handles retries, dead-lettering, and replay; `Integrios.Subscriber` serves the Delivery API, from which Subscriber services pull deliveries for pull Destinations.

A **Connector** is a reusable, deployment-wide declarative contract: it declares what an external
system can do and the bounded choices its instances may configure, and it never holds a concrete
endpoint or credential. Tenants build two independent resources from one: a **Source**, which owns
how events arrive — its transport, verification, and normalization — and a **Destination**, which
owns how to reach an outbound system, its base URI and its authentication. A Source and a
Destination may describe the same external system and still share no identity, lifecycle, or
configuration, because their credentials, runtime behavior, and rate of change differ. Each binds
permanently to the exact Connector version it was created from, so validation and delivery
semantics cannot shift underneath a stable identity.

A **Subscription** carries one delivery operation from a Topic to a Destination: its match rules,
body mapping, HTTP method and path, and its success rule. Generic external event producers are the
universal source path. Delivery is HTTP(S) only: the Worker pushes to a Destination, or a Subscriber
service pulls from the Delivery API. Integrios deliberately does not
require provider-specific destination actions or runtime plugins.

For the full design (processing flow, durability guarantees, and platform concepts), see the [architecture guide](https://arminzou.github.io/integrios/architecture.html).

## Getting Started

Prerequisite: Docker.

```bash
make up
```

This starts Postgres, the setup job (migrations and the first OperatorKey), and the four roles (Admin API on `http://localhost:5150`, Ingestion on `http://localhost:5231`, Delivery API on `http://localhost:5241`). Then follow the [setup guide](https://arminzou.github.io/integrios/setup.html) to onboard a tenant and send your first event end to end. For a production deployment, see [deploy/](deploy/README.md) (Docker Compose) or [deploy/azure](deploy/azure/README.md) (Azure Container Apps).

## Documentation

Read the [Integrios documentation](https://arminzou.github.io/integrios/).

- [Setup & quickstart](https://arminzou.github.io/integrios/setup.html): run locally and deliver your first event
- [Operator dashboard access](https://arminzou.github.io/integrios/operator-dashboard.html): configure OIDC or email-and-password sign-in and recover access
- [Database backends](https://arminzou.github.io/integrios/database-backends.html): PostgreSQL default and SQL Server 2022+ reference configuration
- [GitHub-to-Slack walkthrough](https://arminzou.github.io/integrios/github-to-slack-walkthrough.html): a verified provider webhook
  source through to a transformed destination delivery, end to end
- [Connector manifest reference](https://arminzou.github.io/integrios/connector-manifest.html): every property a Connector manifest
  may carry, and what each one is validated against
- [Architecture](https://arminzou.github.io/integrios/architecture.html): design, processing flow, and platform concepts
- [Observability](https://arminzou.github.io/integrios/observability.html): metrics, traces, logs, and OTLP export
- [CI/CD](https://arminzou.github.io/integrios/ci-cd.html): the pipeline and the published image
- [Contributing](CONTRIBUTING.md)

## Tech Stack

| Area               | Technology                                                                       |
| ------------------ | -------------------------------------------------------------------------------- |
| Language / Runtime | C# / ASP.NET Core (.NET 10)                                                       |
| Database           | PostgreSQL (default) or SQL Server 2022+                                          |
| Event backbone     | Database-backed transactional outbox and work queues                             |
| Observability      | OpenTelemetry (OTLP-capable); Prometheus + Grafana for local dev; bring your own backend |
| Deployment         | Docker / Compose; one container image on GHCR                                     |

## Use Cases

- A durable HTTP ingestion hub for Event producers around payments, CRM, support, commerce, and internal systems
- Tenant-scoped fan-out of one event stream to multiple destinations with per-subscription logic
- Reliable buffering and recovery during downstream outages or rate limiting
- Auditable event and delivery history for compliance and incident response

Integrios is not a no-code workflow builder, broad connector marketplace, ETL platform, API gateway,
or multi-protocol integration runtime.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
