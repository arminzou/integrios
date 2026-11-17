# Database CLI

An operator prepares a database with `integrios admin database`. `migrate` applies EF Core
migrations as the owner, `info` reports applied and pending migrations, and `grant-runtime` grants
the control-plane and data-plane runtime principals their scopes.

## Sub-features

- `db-migrate` applies pending migrations and does nothing when none are pending.
- `db-info` reports `EF migrations: <n> applied, <m> pending.`
- `db-grant-runtime` applies runtime principal scopes from `Database:RuntimePrincipals`.
- `db-usage` prints usage for `--help` and exits `2` for an unknown subcommand.

## How to get to it (user POV)

- `integrios admin database <migrate|info|grant-runtime>`. Here that is
  `$V/control.sh integrios admin database ...`, which connects as the database owner.
- In the Compose stack, `make db-migrate` and `make db-info`.

## Driving it with control.sh

Preconditions:

- `doctor` passes. `up` has already migrated and granted, so every command below runs against a
  current database.
- `mkdir -p $EVIDENCE/database-cli`.

- **Info.** Run `$V/control.sh integrios admin database info > $EVIDENCE/database-cli/info.txt 2>&1; echo "exit $?"`. Exit code `0`, and the file ends with `EF migrations: <n> applied, 0 pending.`
- **Migrate again.** Run `$V/control.sh integrios admin database migrate`. Exit code `0`. The output includes `No migrations were applied. The database is already up to date.` and `database: migrations applied.`
- **Grant again.** Run the `grant-runtime` command from `control.sh up` with the same `Database__RuntimePrincipals__*` variables. Exit code `0` and `database: runtime principal scopes applied.` Admin, Ingestion, and Worker stay ready in `doctor`.
- **Usage.** Run `$V/control.sh integrios admin database --help` and expect usage text. Run `$V/control.sh integrios admin database bogus` and expect exit code `2`.
- **Fresh database.** For a migration change, prove it from empty: run `down`, then `up`. The `migrate` step in the `up` output names each migration it applies.

## Gotchas

- EF Core logs SQL to stdout, so the result line is the last line, not the only one. Match it with
  `grep`.
- Run `migrate` and `grant-runtime` as the owner (`integrios`), never as a runtime principal. The
  runtime principals cannot change the schema.
- Without passwords, `grant-runtime` runs in grant-only mode. Do not pass secrets as command-line
  configuration; use environment variables.
- The local stack uses PostgreSQL. SQL Server needs `Database:Provider` and its own connection
  string (see `docs/database-backends.md`), and `control.sh` does not start it.
