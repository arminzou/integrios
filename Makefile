include .env
export DOTNET_ENVIRONMENT

# --- Docker Compose stack ---

up:
	docker compose up --build -d

down:
	docker compose down

logs:
	docker compose logs -f

# --- Standalone Flyway targets (local Postgres, no compose) ---

db-migrate:
	docker run --rm \
		--network host \
		-e FLYWAY_USER=$(INTEGRIOS_DB_USER) \
		-e FLYWAY_PASSWORD=$(INTEGRIOS_DB_PASSWORD) \
		-v ./db/migrations:/flyway/sql \
		-v ./db/flyway.toml:/flyway/conf/flyway.toml \
		flyway/flyway migrate

db-info:
	docker run --rm \
		--network host \
		-e FLYWAY_USER=$(INTEGRIOS_DB_USER) \
		-e FLYWAY_PASSWORD=$(INTEGRIOS_DB_PASSWORD) \
		-v ./db/migrations:/flyway/sql \
		-v ./db/flyway.toml:/flyway/conf/flyway.toml \
		flyway/flyway info

# --- Admin bootstrap ---
# DOTNET_ENVIRONMENT is set from .env (see export above) so appsettings.Development.json
# is loaded for its Postgres connection string.

# Upsert the built-in webhook integration.
bootstrap-builtins:
	dotnet run --project src/Integrios.Admin -- bootstrap builtins

# Create the global admin key (no-op if a live one already exists).
bootstrap-admin-key:
	dotnet run --project src/Integrios.Admin -- bootstrap admin-key

# Run builtins + admin-key together using the fixed dev secret.
bootstrap-dev:
	dotnet run --project src/Integrios.Admin -- bootstrap dev
