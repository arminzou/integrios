# AGENTS.md

Repository guidelines for AI agents working in this repo.

Read this file first.
If `AGENTS.local.md` exists, read it next for deeper local or private project context.

## Project Overview

Integrios is a backend-only multi-tenant integration platform.
It receives events, applies tenant-aware routing and transformation rules, and delivers work to downstream systems reliably.

## Project Structure

- `Integrios.slnx` is the solution entrypoint.
- `src/` contains the main application projects.
- `src/Integrios.Api` owns HTTP intake, tenant auth, and the durable acceptance boundary.
- `src/Integrios.Worker` owns routing, transformation, delivery, and retry/DLQ behavior.
- `src/Integrios.MockSink` provides a controllable local sink for testing and demos.
- `src/Integrios.Core` holds core domain types and shared contracts.
- `tests/` contains unit and integration test projects.
- `db/migrations/` contains Flyway SQL migrations.
- `docs/` is for public documentation only.

## Architecture

### Conceptual split

- **Control plane**: tenant management, connector configuration, integration flows, routing rules, transformation config, secrets
- **Data plane**: webhook intake, tenant/auth resolution, durable acceptance boundary, outbox, routing, transform, delivery, retry/DLQ/replay

### Core domain model

- `Tenant` is the top-level isolation boundary.
- `ApiCredential` represents machine credentials used to call Integrios APIs.
- `Connector` represents a tenant-scoped configured connection.
- `IntegrationFlow` represents a tenant-owned pipeline.
- `FlowRoute` represents a route within a flow that matches events and delivers to a destination connector.
- `Event` represents an accepted, normalized inbound unit of work.
- `DeliveryAttempt` tracks each delivery attempt for an event.

### Module boundaries

- `Integrios.Api` owns the HTTP surface, tenant resolution, and acceptance-boundary writes. It does not own routing, delivery, or retry behavior.
- `Integrios.Worker` owns routing, transformation, delivery, and retry/DLQ/replay behavior. It does not own HTTP intake.
- `Integrios.MockSink` owns configurable success, failure, and slow responses for local testing. It does not own business logic.
- `Integrios.Core` owns domain entities, enums, and API contracts. It does not own implementation logic.

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
dotnet test tests/Integrios.Api.Tests/Integrios.Api.Tests.csproj

# Run one service
dotnet run --project src/Integrios.Api
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
- `ConnectorType.key` values use `snake_case`

Domain naming:
- keep domain entity names aligned with the model: `Tenant`, `ApiCredential`, `ConnectorType`, `Connector`, `IntegrationFlow`, `FlowRoute`, `Event`, `DeliveryAttempt`

Style:
- prefer early returns over deep nesting
- keep `Integrios.Core` focused on domain entities and contracts
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
- use `AGENTS.local.md` for deeper local context when present
- if docs and code disagree, report it plainly instead of guessing
- keep public docs public and private planning private
