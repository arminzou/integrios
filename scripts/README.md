# Development data seed

`seed-dev-data.sh` replaces the local database's tenant-scoped data with a compact dashboard
dataset. It creates Event API, webhook, and queue Sources, sends real Events through each intake
path, and leaves successful, unrouted, and dead-lettered work for UI testing.

> **Warning:** The script deletes all existing Tenants, Tenant API keys, Connections, Topics,
> Sources, Subscriptions, Events, Deliveries, and delivery attempts from the local development
> database. Connectors and OperatorKeys are preserved.

## Prerequisites

- Docker Desktop
- .NET 10 SDK
- Git Bash with `make`, `curl`, and `jq`

## Run

From Git Bash in the repository root:

```bash
make up
./scripts/seed-dev-data.sh
```

The seed command starts the optional Service Bus emulator automatically. Its first run may take a
few minutes while Docker downloads the emulator and SQL Edge images. The script waits for the
emulator before clearing existing data, then waits for the queue Event and dead-lettered Delivery
before reporting completion.

A successful run ends with database counts and the dashboard URL. The settled dataset contains 11
Events: nine through the Event API, one through a webhook, and one through Azure Service Bus.

Run the script again whenever the dataset needs resetting. When finished with the complete stack:

```bash
docker compose --profile queue-demo down
```
