# AGENTS.md

Repository guidelines for AI agents working in this repo.

Read this file first.

For this repository, optional private context and deeper guidance may live in `.brain/AGENTS.md`.
If it is absent, continue from this public context; its absence is not an error.

## Project Overview

Integrios is an open-source, self-hostable multi-tenant integration platform.
It receives events, applies tenant-aware routing and transformation rules, and delivers work to downstream systems reliably.

It is backend-first today; an Operator-facing admin UI is planned. The engineering team running a
deployment exclusively owns control-plane configuration; Tenants are ownership and isolation
boundaries, not backend users. Observability is a pluggable capability: the platform emits standard
telemetry (metrics, structured logs, OTLP-capable traces) and bundles no backend, so a self-hosting
team points it at their own stack. Licensed under MIT.

## Project Structure

- `Integrios.slnx` is the solution entrypoint.
- `src/` contains the main application projects.
- `src/Integrios.Ingress` owns HTTP intake, tenant auth, and the durable acceptance boundary. Data plane.
- `src/Integrios.Admin` owns tenant management, connection configuration, topic and subscription management. Control plane.
- `src/Integrios.Worker` owns outbox polling, fanout to subscriptions, delivery, and retry/DLQ behavior.
- `src/Integrios.MockSink` provides a controllable local sink for testing and demos. Not part of the deployable product.
- `src/Integrios.Domain` holds core domain types and shared contracts.
- `tests/` contains unit and integration test projects.
- `src/Integrios.Migrations.Postgres/` and `src/Integrios.Migrations.SqlServer/` contain the
  provider-specific EF Core migrations.
- `docs/` is for public documentation only.

## Architecture

### Service split

The platform is divided into two planes across three deployable processes.

- **Control plane** (`Integrios.Admin`, port 5150): Operator-owned Tenant lifecycle, connection configuration, topic and subscription management, and policy. Auth may evolve for Operator identities, but never into Tenant-scoped control-plane authority.
- **Data plane** (`Integrios.Ingress`, port 5231): generic ApiKey-authenticated Event intake plus any curated built-in provider HTTP endpoints, Tenant resolution, source-Connection validation, durable acceptance boundary, and outbox writes. External Event producers remain the universal source path.
- **Worker** (`Integrios.Worker`): outbox polling, fanout to subscriptions, per-subscription delivery, retry/DLQ/replay.

`Integrios.Worker` reads topic and subscription config directly from Postgres. The control plane owns the write path for those tables; the worker holds a read-only contract against them. There are currently no service-to-service config calls.

### Core domain model

- `Tenant` is the top-level ownership and isolation boundary, not a control-plane actor.
- `ApiKey` represents the Integrios-issued machine credential for generic Event intake.
- An `Event producer` is an external Operator-controlled application or automation that converts source-system changes into the generic Event contract and calls Integrios with an ApiKey; it is not an Integrios runtime plugin.
- A `Built-in source adapter` is a platform-supplied HTTP intake module for a popular, stable provider contract; it verifies and normalizes into the same generic Event contract.
- `Connector` is a deployment-wide, reusable declarative HTTP contract for an external-system class. It may be built in or Operator-authored, is shared across tenants, and carries no tenant data.
- `Connection` is a Tenant-owned configured instance of one Connector and carries Tenant-specific endpoint configuration plus separate source-verification and destination-authentication selections and secret references. Its current uses are relationship-derived, never persisted on the Connection.
- `Topic` represents a tenant-owned named stream of events; source connections publish to it.
- `Subscription` represents an independent consumer of a topic with its own filter, transform, versioned HTTP delivery configuration, destination connection, and DLQ scope. Retry policy is currently fixed platform behavior.
- `Event` represents an accepted, normalized inbound unit of work, tagged with the topic it was published to.
- `SubscriptionDelivery` tracks the per-(event, subscription) delivery state produced by fanout.
- `DeliveryAttempt` records each concrete outbound execution against a subscription delivery.

### Connector and HTTP transport model

This is the authoritative source and destination model:

The bullets below describe the finalized product model. The current code has only the built-in
`webhook` Connector, fixed JSON `POST` delivery to `Connection.config.url`, and open,
API-key-header, or bearer-token destination authentication. Connections already persist separate
`source_verification` and `destination_authentication` envelopes. Do not document target
capabilities as shipped until their implementation lands.

- Generic HTTP Event intake through external Event producers is universal. An Event producer authenticates with an ApiKey, identifies a configured source Connection, and publishes to an allowed Topic.
- Integrios may ship a small curated set of built-in provider HTTP source adapters when a stable, popular contract materially reduces repeated security or operational work. Every adapter must normalize into the same Event acceptance seam.
- Operator-authored Connectors cannot contain executable source code. Do not add runtime plugins, polling adapters, or a broad connector catalog without a new architecture decision.
- HTTP(S) is the only destination protocol in current scope. Do not introduce non-HTTP destination protocols, a runtime plugin system, or provider-specific execution adapters without a new architecture decision.
- Connectors are narrow declarative definitions: stable identity, presentation metadata, direction, required Connection fields, supported auth schemes, and optional base-URI constraints. They do not own executable request templates, transforms, routing, retries, or workflow steps.
- The same Connector is reusable across Tenants. Each Tenant supplies its own Connection configuration and secret references.
- A Connection's use is derived from active relationships: a source association with an active Topic is a source use, and an active Subscription reference is a destination use. Disabled relationships do not contribute a use. Do not call this a Connection role, capability, or direction.
- A destination Connection owns the absolute base URI and authentication. A Subscription owns HTTP method, relative path or path expression, restricted static headers, and JSON-or-no-body behavior.
- Initial methods are `POST`, `PUT`, `PATCH`, and `DELETE`. Dynamic headers, arbitrary methods, form/multipart/binary bodies, response-driven workflows, and successful-response persistence are out of scope.
- OAuth 2.0 client credentials belongs to the reusable Connection auth-scheme set; interactive OAuth flows do not.
- Fanout snapshots versioned HTTP and non-secret Connection configuration plus secret references. The Worker resolves current secret values per attempt. Never persist resolved values in execution snapshots.
- Destination actions are not part of the current domain model. Multiple external updates are modeled as multiple independent Subscriptions, preserving separate retry, DLQ, and replay behavior.
- Once referenced by a Connection, a Connector's functional contract is immutable. Breaking contract changes require a new version or definition; presentation metadata may still change.

### Module boundaries

- `Integrios.Ingress` owns the HTTP surface, tenant resolution, and acceptance-boundary writes. It does not own fanout, delivery, or retry behavior.
- `Integrios.Admin` owns control plane configuration. It does not own event processing.
- `Integrios.Worker` owns outbox polling, fanout to subscriptions, per-subscription delivery, and retry/DLQ/replay. It does not own HTTP intake or config writes.
- `Integrios.MockSink` owns controllable success, failure, and slow-path responses for local testing. It is never a dependency of production services.
- `Integrios.Domain` owns domain entities, enums, and API contracts. It does not own implementation logic.

### Scope constraints

These are current scope boundaries, not permanent non-goals; the Operator Admin UI deliberately relaxes the frontend constraint when it lands. Phase sequencing lives in the private roadmap.

- backend-first (the Operator Admin UI is planned, not yet in scope)
- no frontend, login/session, or RBAC yet
- no required `User` domain entity
- tenant-aware design from the start
- idempotency, replayability, retries, and DLQ are platform concerns
- keep domain language generic, not company-specific

## Build and Test Commands

```bash
# Build the whole solution
dotnet build Integrios.slnx

# Run all tests
dotnet test Integrios.slnx

# Run one test project
dotnet test tests/Integrios.Ingress.Tests/Integrios.Ingress.Tests.csproj

# Run one service
dotnet run --project src/Integrios.Ingress
dotnet run --project src/Integrios.Admin
dotnet run --project src/Integrios.Worker
dotnet run --project src/Integrios.MockSink
```

## Database Commands

```bash
make db-info
make db-migrate
```

No `.env` is needed; the Makefile and `compose.yml` default all dev values. A repo-root `.env`
overrides them.

## Code Style Guidelines

Language and platform:

- C# / .NET
- nullable enabled
- implicit usings enabled

Naming:

- PascalCase for types, methods, and properties
- camelCase for locals and parameters
- database columns use `snake_case`
- `Connector.key` values use `snake_case`

Domain naming:

- keep domain entity names aligned with the model: `Tenant`, `ApiKey`, `Connector`, `Connection`, `Topic`, `Subscription`, `Event`, `SubscriptionDelivery`, `DeliveryAttempt`

Style:

- prefer early returns over deep nesting
- keep `Integrios.Domain` focused on domain entities and contracts
- do not hide architectural decisions in code without updating the right docs
- do not mistake scaffold/template code for intended final architecture

## Testing Instructions

- use xUnit for tests
- when changing behavior, add or update tests where practical
- prefer targeted tests for narrow changes and full-suite runs for broader changes
- if you skip verification, say so explicitly

Default verification:

- docs-only change: verify referenced files and paths exist
- code change: run the most relevant build/test commands for the touched area
- schema or architecture change: verify migrations, tests, and docs stay aligned

## Commit Guidelines

Use Conventional Commits:

```text
<type>(<scope>): <description>
```

Common types:

- `feat`
- `fix`
- `refactor`
- `test`
- `docs`
- `chore`
- `perf`

Suggested scopes:

- `api`
- `admin`
- `worker`
- `mocksink`
- `core`
- `db`
- `docs`
- `infra`

Examples:

```text
feat(api): add webhook intake endpoint with tenant resolution
fix(worker): handle null payload in delivery attempt tracker
docs: update domain model overview
chore(db): add initial migration for tenant and connector tables
```

## Agent Notes

- read the minimum code and docs needed to avoid guessing
- if docs and code disagree, report it plainly instead of guessing
- keep public docs public and private planning private
