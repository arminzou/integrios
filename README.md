# Integrios

[![CI](https://github.com/arminzou/integrios/actions/workflows/ci.yml/badge.svg)](https://github.com/arminzou/integrios/actions/workflows/ci.yml)

Integrios is an open-source, self-hostable, Operator-run HTTP integration platform. It gives
engineering teams a durable boundary for accepting events, Tenant-aware routing and transformation,
and reliable HTTP delivery with retries, dead-lettering, replay, and auditable delivery history.

## Features

- Durable event intake behind a transactional-outbox acceptance boundary, so no accepted event is lost
- ApiKey-authenticated generic intake with Tenant isolation
- Topic/subscription routing with optional JSONata payload transforms
- Authenticated HTTP JSON delivery using open, API-key-header, or bearer-token authentication
- Reliable async delivery with bounded retries, dead-lettering, and replay
- Per-event status and delivery-attempt history
- Pluggable, vendor-neutral observability (OpenTelemetry metrics, logs, traces); bring your own backend
- Clean control-plane / data-plane / worker separation; scales horizontally
- Self-hostable and Docker-first

## Architecture

Integrios splits platform intent from runtime execution. The **control plane** (`Integrios.Admin`) owns tenants, integrations, connections, topics, and subscriptions. The **data plane** takes over at runtime: `Integrios.Ingress` validates, authenticates, and durably accepts events behind a transactional outbox; `Integrios.Worker` fans out to subscriptions, applies transforms, delivers to destination connections, and handles retries, dead-lettering, and replay.

An **Integration** is a reusable, deployment-wide declarative HTTP contract. A **Connection** is a
Tenant-owned configured instance of one Integration, so the same Integration can serve many Tenants
without sharing their endpoints or credentials. Generic external Event producers are the universal
source path; HTTP(S) is the only destination protocol. Integrios deliberately does not require
provider-specific destination actions or runtime plugins.

For the full design (processing flow, durability guarantees, and platform concepts), see [docs/architecture.md](docs/architecture.md).

## Getting Started

Prerequisite: Docker.

```bash
make up
```

This starts the services, Postgres, migrations, and a test sink (Admin API on `http://localhost:5150`, Ingress on `http://localhost:5231`). Then follow the [setup guide](docs/setup.md) to onboard a tenant and send your first event end to end. For a production deployment, see [deploy/](deploy/README.md).

## Documentation

- [Setup & quickstart](docs/setup.md): run locally and deliver your first event
- [Database backends](docs/database-backends.md): PostgreSQL default and SQL Server 2022+ reference configuration
- [GitHub-to-Slack walkthrough](docs/github-to-slack-walkthrough.md): a verified provider webhook
  source through to a transformed destination delivery, end to end
- [Architecture](docs/architecture.md): design, processing flow, and platform concepts
- [Observability](docs/observability.md): metrics, traces, logs, and OTLP export
- [CI/CD](docs/ci-cd.md): the pipeline and published images
- [Contributing](CONTRIBUTING.md)

## Tech Stack

| Area               | Technology                                                                       |
| ------------------ | -------------------------------------------------------------------------------- |
| Language / Runtime | C# / ASP.NET Core (.NET 10)                                                       |
| Database           | PostgreSQL (default) or SQL Server 2022+                                          |
| Event backbone     | Database-backed transactional outbox and work queues                             |
| Observability      | OpenTelemetry (OTLP-capable); Prometheus + Grafana for local dev; bring your own backend |
| Deployment         | Docker / Compose; container images on GHCR                                        |

## Use Cases

- A durable HTTP ingress hub for Event producers around payments, CRM, support, commerce, and internal systems
- Tenant-scoped fan-out of one event stream to multiple destinations with per-subscription logic
- Reliable buffering and recovery during downstream outages or rate limiting
- Auditable event and delivery history for compliance and incident response

Integrios is not a no-code workflow builder, broad connector marketplace, ETL platform, API gateway,
or multi-protocol integration runtime.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
