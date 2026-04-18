# CLAUDE.md

This file provides guidance to agents when working in this repository.

## Project Structure

```
integrios/
├── Integrios.slnx          # Solution file
├── src/
│   ├── Integrios.Api/      # HTTP intake, tenant auth, durable acceptance boundary
│   ├── Integrios.Worker/   # Event routing, transformation, delivery, retry/DLQ
│   ├── Integrios.MockSink/ # Local controllable sink for testing
│   └── Integrios.Core/     # Domain model, shared contracts
├── tests/
│   ├── Integrios.Api.Tests/
│   ├── Integrios.Worker.Tests/
│   └── Integrios.IntegrationTests/
├── db/
│   ├── migrations/
│   ├── schema/
│   └── queries/
└── docs/
```

## Architecture

Integrios is a multi-tenant integration platform. It receives events, applies tenant-aware routing and transformation rules, and delivers work to downstream systems reliably.

### Control Plane vs Data Plane

The platform is split into two conceptual planes:

- **Control plane** — tenant management, connector configuration, integration flows, routing rules, transformation config, secrets
- **Data plane** — webhook intake, tenant/auth resolution, durable acceptance boundary, outbox → Kafka → routing → transform → delivery → retry/DLQ/replay

### Domain Model

```
Tenant
  ├── ApiCredential          (machine credentials for calling Integrios APIs)
  ├── Connector              (tenant-scoped connection, via ConnectorType)
  └── IntegrationFlow
        └── FlowRoute        (match rules, transform config, delivery policy)
              └── → destination Connector

Event                        (normalized inbound unit of work)
  └── DeliveryAttempt        (one attempt per route per event)
```

**Key entities:**
- `Tenant` — top-level isolation boundary; owns all resources
- `ConnectorType` — platform-level definition (e.g. `webhook`, `dynamics_crm`) — direction, auth scheme, config schema
- `Connector` — tenant-scoped configured connection using one `ConnectorType`
- `IntegrationFlow` — tenant-owned pipeline scoped to one source connector and a list of event types
- `FlowRoute` — branch within a flow; matches events and delivers to one destination connector
- `Event` — accepted, normalized inbound record with idempotency key and status
- `DeliveryAttempt` — tracks each delivery attempt with request/response detail, attempt number, retry scheduling

### Data Flow

```
Ingest → validate + resolve tenant → authenticate (ApiCredential)
  → write to Postgres (durable acceptance boundary)
  → outbox → Kafka
  → route by flow config → transform → deliver via connector
  → track DeliveryAttempt → retry / DLQ / replay
```

### Module Boundaries

| Project | Owns | Does not own |
|---------|------|--------------|
| `Integrios.Api` | HTTP surface, tenant resolution, acceptance boundary writes | Routing logic, delivery, retry |
| `Integrios.Worker` | Routing, transformation, delivery, retry/DLQ/replay | HTTP intake, direct DB writes outside outbox |
| `Integrios.MockSink` | Configurable success/failure/slow responses for local testing | Business logic |
| `Integrios.Core` | Domain entities, enums, API contracts | Implementation logic, transport models |

### V1 Scope Constraints

- Backend-only; no frontend, no login/session, no RBAC
- No `User` domain entity — use `ApiCredential` for machine auth, `Tenant` as the account boundary
- Idempotency, replayability, retries, and DLQ are platform-level concerns, not per-connector
- Keep domain generic — no company-specific business logic

## Build, Test, and Development Commands

```bash
# Build entire solution
dotnet build Integrios.slnx

# Run all tests
dotnet test Integrios.slnx

# Run a single test project
dotnet test tests/Integrios.Api.Tests/Integrios.Api.Tests.csproj

# Run a specific test by name
dotnet test Integrios.slnx --filter "FullyQualifiedName~TestClassName.MethodName"

# Run a specific service
dotnet run --project src/Integrios.Api
dotnet run --project src/Integrios.Worker
dotnet run --project src/Integrios.MockSink
```

## Coding Style & Naming Conventions

**Language:** C# / .NET 10. All projects use `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`.

**Naming:**
- PascalCase for types, methods, and properties
- camelCase for local variables and parameters
- Domain entity names must match the model exactly: `Tenant`, `ApiCredential`, `ConnectorType`, `Connector`, `IntegrationFlow`, `FlowRoute`, `Event`, `DeliveryAttempt`
- Database columns use `snake_case`
- `ConnectorType.key` values use `snake_case` (e.g. `webhook`, `dynamics_crm`)

**Code style:**
- Prefer early returns over nested conditionals
- Keep `Integrios.Core` to domain entities and contracts — no implementation logic
- Tests use xunit

## Commit Guidelines

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <description>
```

**Types:** `feat`, `fix`, `chore`, `refactor`, `test`, `docs`, `perf`

**Scopes** match the affected project or layer: `api`, `worker`, `mocksink`, `core`, `db`, `infra`, `docs`

**Examples:**
```
feat(api): add webhook intake endpoint with tenant resolution
fix(worker): handle null payload in delivery attempt tracker
chore(db): add initial migration for tenant and connector tables
test(api): add unit tests for tenant auth middleware
docs: update architecture overview with outbox flow
```
