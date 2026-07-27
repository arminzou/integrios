# Integrios

[![CI](https://github.com/arminzou/integrios/actions/workflows/ci.yml/badge.svg)](https://github.com/arminzou/integrios/actions/workflows/ci.yml)

Integrios is an open-source, self-hostable backend integration platform. It gives engineering teams a durable boundary for receiving events, tenant-aware routing and transformation, and reliable delivery to downstream systems, with retries, dead-lettering, replay, and end-to-end observability. Run it yourself and adapt it to whatever you need to integrate.

> [!NOTE]
> **Early preview, built incrementally.** The backend foundation works end to end and is backend-first (an Operator-facing admin UI is planned). Evaluate it as a self-hostable foundation, not a turnkey production deployment. MIT licensed.
>
> Not yet, but planned:
>
> - **Provider-native integrations.** Generic HTTP delivery supports open, API-key-header, and bearer-token authentication; provider-native webhooks, polling, OAuth lifecycle, and actions are still to come.
> - **No admin UI.** The Operator configures every Tenant and integration through the Admin API.
> - **No Operator RBAC or rate limiting yet.** Tenant isolation is enforced at the data layer; Tenants do not receive control-plane access.

## Features

- Durable event intake behind a transactional-outbox acceptance boundary, so no accepted event is lost
- ApiKey-authenticated generic intake with Tenant isolation
- Topic/subscription routing with optional JSONata payload transforms
- Reliable async delivery: bounded retries, dead-lettering, and replay
- Per-event status and delivery-attempt history
- Pluggable, vendor-neutral observability (OpenTelemetry metrics, logs, traces); bring your own backend
- Clean control-plane / data-plane / worker separation; scales horizontally
- Self-hostable and Docker-first

## Architecture

![Integrios architecture](docs/assets/architecture-diagram.png)

Integrios splits platform intent from runtime execution. The **control plane** (`Integrios.Admin`) owns tenants, integrations, connections, topics, and subscriptions. The **data plane** takes over at runtime: `Integrios.Ingress` validates, authenticates, and durably accepts events behind a transactional outbox; `Integrios.Worker` fans out to subscriptions, applies transforms, delivers to destination connections, and handles retries, dead-lettering, and replay.

For the full design (processing flow, durability guarantees, and platform concepts), see [docs/architecture.md](docs/architecture.md).

## Getting Started

Prerequisite: Docker.

```bash
make up
```

This starts the services, Postgres, migrations, and a test sink (Admin API on `http://localhost:5150`, Ingress on `http://localhost:5231`). Then follow the [setup guide](docs/setup.md) to onboard a tenant and send your first event end to end. For a production deployment, see [deploy/](deploy/README.md).

## Documentation

- [Setup & quickstart](docs/setup.md): run locally and deliver your first event
- [Architecture](docs/architecture.md): design, processing flow, and platform concepts
- [Observability](docs/observability.md): metrics, traces, logs, and OTLP export
- [CI/CD](docs/ci-cd.md): the pipeline and published images
- [Contributing](CONTRIBUTING.md)

## Tech Stack

| Area               | Technology                                                                       |
| ------------------ | -------------------------------------------------------------------------------- |
| Language / Runtime | C# / ASP.NET Core (.NET 10)                                                       |
| Database           | PostgreSQL                                                                        |
| Event backbone     | PostgreSQL transactional outbox + bus behind `IEventBus` (Kafka swappable when justified) |
| Observability      | OpenTelemetry (OTLP-capable); Prometheus + Grafana for local dev; bring your own backend |
| Deployment         | Docker / Compose; container images on GHCR                                        |

## Use Cases

- A central ingress hub for webhook-heavy ecosystems (payments, CRM, support, commerce, internal systems)
- Tenant-scoped fan-out of one event stream to multiple destinations with per-subscription logic
- Reliable buffering and recovery during downstream outages or rate limiting
- Auditable event and delivery history for compliance and incident response

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
