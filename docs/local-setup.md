# Local Setup

Run the full Integrios stack locally using Docker Compose.

## Prerequisites

- Docker with Compose plugin
- `make`

## Quick start

```bash
cp .env.example .env
make up
```

This builds all images, starts Postgres, runs Flyway migrations, then starts Ingress, Admin, Worker, and MockSink.

## Service endpoints

| Service   | URL                        | Purpose                         |
|-----------|----------------------------|---------------------------------|
| Ingress   | http://localhost:5231      | Webhook intake (data plane)     |
| Admin     | http://localhost:5150      | Tenant/config management        |
| MockSink  | http://localhost:5054      | Controllable delivery target    |

Worker runs in the background with no HTTP port.

## Environment variables

Copy `.env.example` to `.env` and edit as needed:

| Variable            | Used by              | Purpose                              |
|---------------------|----------------------|--------------------------------------|
| `POSTGRES_USER`     | compose, Postgres    | Database username                    |
| `POSTGRES_PASSWORD` | compose, Postgres    | Database password                    |
| `INTEGRIOS_DB_USER` | Makefile db-* targets| Same user, for bare Flyway commands  |
| `INTEGRIOS_DB_PASSWORD` | Makefile db-* targets | Same password                   |

For a local demo, the defaults in `.env.example` are fine.

## Useful commands

```bash
make up      # build and start all services (detached)
make down    # stop and remove containers
make logs    # tail all service logs
```

## End-to-end smoke test

After `make up`, use the Admin API to create a tenant, topic, subscription, and connection pointing at MockSink, then POST a webhook to Ingress. The Worker will pick it up and deliver to MockSink at `http://mocksink:8080/sink/<name>`.

Note: inside Docker Compose, services reach MockSink at `http://mocksink:8080`. From your host you can reach it at `http://localhost:5054`.

## Migrations

Migrations run automatically as part of `make up` via the `migrate` service. To run them manually against a local Postgres:

```bash
make db-migrate
make db-info
```

These use `--network host` and expect Postgres on `localhost:5432`.
