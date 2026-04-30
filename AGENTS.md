# AGENTS.md

Repository guidelines for AI agents working in this repo.

Read this file first.

For this repository, private context and deeper guidance live in `dev/AGENTS.md`.

## Project Overview

Integrios is a backend-only multi-tenant integration platform.
It receives events, applies tenant-aware routing and transformation rules, and delivers work to downstream systems reliably.

## Project Structure

- `Integrios.slnx` is the solution entrypoint.
- `src/` contains the main application projects.
- `src/Integrios.Ingress` owns HTTP intake, tenant auth, and the durable acceptance boundary. Data plane.
- `src/Integrios.Admin` owns tenant management, connection configuration, topic and subscription management. Control plane.
- `src/Integrios.Worker` owns outbox polling, fanout to subscriptions, delivery, and retry/DLQ behavior.
- `src/Integrios.MockSink` provides a controllable local sink for testing and demos. Not part of the deployable product.
- `src/Integrios.Domain` holds core domain types and shared contracts.
- `tests/` contains unit and integration test projects.
- `db/migrations/` contains Flyway SQL migrations.
- `docs/` is for public documentation only.

## Architecture

### Service split

The platform is divided into two planes, each a separate ASP.NET service with its own `Program.cs` and port.

- **Control plane** (`Integrios.Admin`, port 5150): tenant lifecycle, connection configuration, topic and subscription management, policy. Auth will diverge from the data plane (admin tokens, human sessions) as the platform grows.
- **Data plane** (`Integrios.Ingress`, port 5231): webhook intake, tenant/auth resolution, durable acceptance boundary, outbox writes (publishes accepted events to a topic in the same transaction).
- **Worker** (`Integrios.Worker`): outbox polling, fanout to subscriptions, per-subscription delivery, retry/DLQ/replay.

`Integrios.Worker` reads topic and subscription config directly from Postgres. The control plane owns the write path for those tables; the worker holds a read-only contract against them. There are no service-to-service config calls in v1. See `dev/decisions.md` for the rationale and migration path.

### Core domain model

- `Tenant` is the top-level isolation boundary.
- `ApiKey` represents machine credentials used to call Integrios APIs.
- `Integration` represents a reusable platform-level definition of how to talk to a system.
- `Connection` represents a tenant-scoped configured connection.
- `Topic` represents a tenant-owned named stream of events; source connections publish to it.
- `Subscription` represents an independent consumer of a topic with its own filter, destination connection, retry policy, and DLQ scope.
- `Event` represents an accepted, normalized inbound unit of work, tagged with the topic it was published to.
- `SubscriptionDelivery` tracks the per-(event, subscription) delivery state produced by fanout.
- `DeliveryAttempt` records each concrete outbound execution against a subscription delivery.

### Module boundaries

- `Integrios.Ingress` owns the HTTP surface, tenant resolution, and acceptance-boundary writes. It does not own fanout, delivery, or retry behavior.
- `Integrios.Admin` owns control plane configuration. It does not own event processing.
- `Integrios.Worker` owns outbox polling, fanout to subscriptions, per-subscription delivery, and retry/DLQ/replay. It does not own HTTP intake or config writes.
- `Integrios.MockSink` owns controllable success, failure, and slow-path responses for local testing. It is never a dependency of production services.
- `Integrios.Domain` owns domain entities, enums, and API contracts. It does not own implementation logic.

### Version 1 constraints

- backend-only
- no frontend, login/session, or RBAC
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
cp .env.example .env
make db-info
make db-migrate
```

## Code Style Guidelines

Language and platform:

- C# / .NET
- nullable enabled
- implicit usings enabled

Naming:

- PascalCase for types, methods, and properties
- camelCase for locals and parameters
- database columns use `snake_case`
- `Integration.key` values use `snake_case`

Domain naming:

- keep domain entity names aligned with the model: `Tenant`, `ApiKey`, `Integration`, `Connection`, `Topic`, `Subscription`, `Event`, `SubscriptionDelivery`, `DeliveryAttempt`

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
