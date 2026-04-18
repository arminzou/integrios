include .env

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
