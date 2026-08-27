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
- `src/Integrios.Ingestion` owns intake, tenant auth, and the durable acceptance boundary. Data plane.
- `src/Integrios.Admin` owns tenant management, connection configuration, topic and subscription management. Control plane.
- `src/Integrios.Worker` owns outbox polling, fanout to subscriptions, delivery, and retry/DLQ behavior.
- The development Compose stack includes WireMock as a controllable local sink for testing and demos. It is not part of the deployable product.
- `src/Integrios.Domain` holds core domain types and shared contracts.
- `tests/` contains the test projects; a project's name states what it needs to run.
- `src/Integrios.Migrations.Postgres/` and `src/Integrios.Migrations.SqlServer/` contain the
  provider-specific EF Core migrations.
- `docs/` is for public documentation only.

## Architecture

The code is authoritative for the domain model, its vocabulary, and the current source and
destination capabilities. Do not restate them here: a copy in this file drifts silently and is then
read as instruction. Narrative descriptions belong in `docs/`, which may lag the code and is not a
substitute for reading it.

### Service split

The platform is divided into two planes across three deployable processes.

- **Control plane** (`Integrios.Admin`, port 5150): Operator-owned configuration and policy. Auth may
  evolve for Operator identities, but never into Tenant-scoped control-plane authority.
- **Data plane** (`Integrios.Ingestion`, port 5231): authenticated intake, resolution of the
  configured origin, the durable acceptance boundary, and outbox writes.
- **Worker** (`Integrios.Worker`): outbox polling, fanout, per-subscription delivery, and
  retry/DLQ/replay.

The Worker reads configuration directly from the configured database. The control plane owns the
write path for those tables; the Worker holds a read-only contract against them. There are no
service-to-service configuration calls.

### Module boundaries

- `Integrios.Ingestion` owns the intake surface, tenant resolution, and acceptance-boundary writes. It does not own fanout, delivery, or retry behavior.
- `Integrios.Admin` owns control plane configuration. It does not own event processing.
- `Integrios.Worker` owns outbox polling, fanout to subscriptions, per-subscription delivery, and retry/DLQ/replay. It does not own HTTP intake or config writes.
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
dotnet test tests/Integrios.Ingestion.UnitTests/Integrios.Ingestion.UnitTests.csproj

# Run one service
dotnet run --project src/Integrios.Ingestion
dotnet run --project src/Integrios.Admin
dotnet run --project src/Integrios.Worker
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

Style:

- prefer early returns over deep nesting
- keep `Integrios.Domain` focused on domain entities and contracts
- do not hide architectural decisions in code without updating the right docs
- do not mistake scaffold/template code for intended final architecture

## Testing Instructions

- use xUnit for tests
- when changing behavior, add or update tests where practical
- if you skip verification, say so explicitly

A test project's name states what it needs to run. `Integrios.<Layer>.UnitTests` needs nothing;
`Integrios.FunctionalTests` starts databases and a message-broker emulator through
Testcontainers; `Integrios.AcceptanceTests` builds every service image and composes the packaged
deployment. Cost follows that ordering, and acceptance dominates a run of everything.

Choose tests by what the change can actually break, not by default:

| Change | Run |
|---|---|
| anything at all | `UnitTests` projects + `ArchitectureTests` |
| behavior in `src/` | add `FunctionalTests` on the default provider |
| raw SQL, JSON operators, migrations, or the `DbContext` | add the second provider leg, `INTEGRIOS_TEST_DATABASE_PROVIDER=sqlserver` |
| host composition, dependency registration, Dockerfile, Compose, bootstrap, or an HTTP contract the acceptance tests exercise | add `AcceptanceTests` |

The second provider leg is for plausible provider divergence, not reassurance: one provider catches
nearly everything that is not provider-specific.

CI runs `AcceptanceTests` through the `acceptance` job on every non-PR run (`run_kind` `main`,
`nightly`, or `release`). Running it locally before each commit in a batch duplicates a gate that is
about to run anyway; run it once before pushing, or when the change is in the list above.

While iterating, filter to the narrowest relevant test rather than running a whole project:
`dotnet test <project> --filter "FullyQualifiedName~SourcesAdminTests"`. Do not partition a project
with traits or category filters.

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
