# Zero-config dev: defaults below match compose.yml; an optional .env overrides them.
-include .env
POSTGRES_USER ?= integrios
POSTGRES_PASSWORD ?= integrios_dev
DOTNET_ENVIRONMENT ?= Development
INTEGRIOS_BOOTSTRAP_ADMIN_SECRET ?= admin_bootstrap_secret
export DOTNET_ENVIRONMENT
export INTEGRIOS_BOOTSTRAP_ADMIN_SECRET

# --- Docker Compose stack ---

up:
	docker compose up --build -d

down:
	docker compose down

logs:
	docker compose logs -f

# --- Database migrations through the Compose network ---

db-migrate:
	docker compose run --rm migrate migrate

db-info:
	docker compose run --rm migrate info

# --- Admin bootstrap ---
# DOTNET_ENVIRONMENT and INTEGRIOS_BOOTSTRAP_ADMIN_SECRET are exported above
# so appsettings.Development.json and the dev admin secret are picked up.

# Upsert the built-in webhook integration.
bootstrap-builtins:
	dotnet run --project src/Integrios.Admin -- bootstrap --builtins

# Create the global admin key (no-op if a live one already exists).
bootstrap-admin-key:
	dotnet run --project src/Integrios.Admin -- bootstrap --admin-key

# Run builtins + admin-key together.
bootstrap:
	dotnet run --project src/Integrios.Admin -- bootstrap
