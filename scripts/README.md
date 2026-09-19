# Development data seed

Two equivalent scripts replace the local database's tenant-scoped data with a compact dashboard
dataset. They create Event API, webhook, and (optionally) queue Sources, send real Events through
each intake path, and leave successful, unrouted, and dead-lettered work for UI testing.

| Environment | Script |
| --- | --- |
| Windows (recommended) | `Seed-DevData.ps1` — PowerShell 7, no `jq`/`curl`/Git Bash needed |
| POSIX / CI | `seed-dev-data.sh` — Bash with `curl` and `jq` |

Keep the two in sync when changing the dataset.

> **Warning:** Both scripts delete all existing Tenants, Tenant API keys, Destinations, Topics,
> Sources, Subscriptions, Events, and Deliveries from the local development database. Connectors
> and OperatorKeys are preserved.

## Prerequisites

- Docker Desktop, with the stack running via `make up`
- PowerShell 7 (`pwsh`) for the PowerShell script, or Bash with `curl` and `jq` for the shell script
- .NET 10 SDK only when seeding the optional queue path

## Run

PowerShell 7, from the repository root:

```powershell
./scripts/Seed-DevData.ps1
```

Bash, from the repository root:

```bash
./scripts/seed-dev-data.sh
```

A successful run ends with database counts and the dashboard URL.

The settled dataset without the queue path contains 10 Events: nine through the Event API and one
through a webhook.

## Optional queue demo

The queue path is opt-in because it starts the Service Bus emulator, whose first run downloads two
Docker images and can take a few minutes. Enable it to add a broker-backed Source and an eleventh
Event through Azure Service Bus:

```powershell
./scripts/Seed-DevData.ps1 -QueueDemo
```

```bash
INTEGRIOS_SEED_QUEUE_DEMO=1 ./scripts/seed-dev-data.sh
```

The script waits for the emulator before clearing existing data, then waits for the queue Event and
the dead-lettered Delivery before reporting completion.

Run either script again whenever the dataset needs resetting. When finished with the emulator:

```bash
docker compose --profile queue-demo down
```
