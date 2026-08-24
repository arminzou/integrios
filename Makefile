# Zero-config dev: defaults below match compose.yml; an optional .env overrides them.
-include .env
POSTGRES_USER ?= integrios
POSTGRES_PASSWORD ?= integrios_dev
DOTNET_ENVIRONMENT ?= Development
INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET ?= operator_bootstrap_secret
export DOTNET_ENVIRONMENT
export INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET

# --- Docker Compose stack ---

up:
	docker compose up --build -d

down:
	docker compose down

logs:
	docker compose logs -f

# --- Database migrations through the Compose network ---

db-migrate:
	docker compose run --rm migrate

db-info:
	docker compose run --rm migrate database info

# --- Admin bootstrap ---
# DOTNET_ENVIRONMENT and INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET are exported above
# so appsettings.Development.json and the dev OperatorKey secret are picked up.

# Upsert the built-in webhook connector.
bootstrap-builtins:
	dotnet run --project src/Integrios.Admin -- bootstrap --builtins

# Create the global operator key (no-op if a live one already exists).
bootstrap-operator-key:
	dotnet run --project src/Integrios.Admin -- bootstrap --operator-key

# Run builtins + operator-key together.
bootstrap:
	dotnet run --project src/Integrios.Admin -- bootstrap
