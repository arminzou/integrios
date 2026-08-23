# Database backends

Integrios supports PostgreSQL and SQL Server 2022 or later. PostgreSQL is the default: omitting
`Database__Provider` selects it, and the root Compose quickstart starts PostgreSQL without extra
configuration. SQL Server 2022+ is the Microsoft-stack reference deployment.

| Provider | `Database__Provider` | Connection string key |
|---|---|---|
| PostgreSQL | `postgres` or omitted | `ConnectionStrings__Postgres` |
| SQL Server 2022+ | `sqlserver` | `ConnectionStrings__SqlServer` |

Set the same provider and connection string on the migration one-shot, Admin, Ingress, and Worker.
For example:

```text
Database__Provider=sqlserver
ConnectionStrings__SqlServer=Server=sql.example;Database=integrios;User Id=integrios;Password=<secret>;Encrypt=True
```

Before starting the services, run the matching Admin image once with `database migrate`. The
command uses EF Core migrations for the selected provider and takes a database-level migration
lock. The first EF-managed release requires an empty database and does not adopt schemas created by
the former Flyway migration path; subsequent EF-managed releases migrate normally.

The SQL Server work queues run at `READ COMMITTED` and use locking hints that work with
`READ_COMMITTED_SNAPSHOT` either `ON` or `OFF`. Operators do not need to change that database option
for Integrios. The claim queries combine `READPAST` with `READCOMMITTEDLOCK`, as required by
[Microsoft's table-hint guidance](https://learn.microsoft.com/sql/t-sql/queries/hints-transact-sql-table#readpast-transact-sql).

Database selection changes persistence only. Connectors—including future Dataverse source and
destination support—use the same HTTP, OAuth, Event, and delivery contracts with either backend.
