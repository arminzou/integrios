# Zero-config dev: defaults below match compose.yml; an optional .env overrides them.
-include .env
DOTNET_ENVIRONMENT ?= Development
INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET ?= operator_bootstrap_secret
export DOTNET_ENVIRONMENT
export INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET

.PHONY: up up-observability down logs db-migrate db-info bootstrap dashboard

# --- Docker Compose stack ---

up:
	docker compose up --build -d

up-observability:
	docker compose --profile observability up --build -d

down:
	docker compose -f compose.yml -f scripts/sink/compose.yml down

logs:
	docker compose logs -f

# --- Database migrations through the Compose network ---

db-migrate:
	docker compose run --rm setup database migrate

db-info:
	docker compose run --rm setup database info

# --- Admin bootstrap ---
# DOTNET_ENVIRONMENT and INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET are exported above
# so appsettings.admin.Development.json and the dev OperatorKey secret are picked up.

# Create the first global OperatorKey (no-op if a live one already exists).
bootstrap:
	cd src/Integrios.Admin && dotnet run --project ../Integrios --launch-profile admin -- admin bootstrap

# --- Operator dashboard dev server ---
# Vite on :5173, proxying /admin and /auth to the Admin host from `make up`.
dashboard:
	cd src/Integrios.Admin/frontend && npm ci && npm run dev
