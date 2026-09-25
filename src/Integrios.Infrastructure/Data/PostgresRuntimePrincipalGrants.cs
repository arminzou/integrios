using Dapper;
using System.Data.Common;
using Npgsql;

namespace Integrios.Infrastructure.Data;

internal static class PostgresRuntimePrincipalGrants
{
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<RuntimePrincipal> principals,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        CurrentIdentity identity = await connection.QuerySingleAsync<CurrentIdentity>(new CommandDefinition(
            """
            SELECT current_database() AS DatabaseName,
                   current_user AS CurrentUser,
                   session_user AS SessionUser;
            """,
            transaction: transaction,
            cancellationToken: cancellationToken));

        var existing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (RuntimePrincipal principal in principals)
        {
            TargetGuard? guard = await connection.QuerySingleOrDefaultAsync<TargetGuard>(new CommandDefinition(
                """
                SELECT r.rolname AS Name,
                       r.rolsuper
                       OR r.rolcreaterole
                       OR r.rolcreatedb
                       OR r.rolreplication
                       OR r.rolbypassrls
                       OR r.rolname = @CurrentUser
                       OR r.rolname = @SessionUser
                       OR r.oid = (SELECT datdba FROM pg_database WHERE datname = current_database())
                       OR pg_has_role(r.oid, (SELECT nspowner FROM pg_namespace WHERE nspname = 'public'), 'MEMBER')
                       OR COALESCE(pg_has_role(r.oid, to_regrole('azure_pg_admin'), 'MEMBER'), false)
                       AS IsPrivileged
                FROM pg_roles r
                WHERE r.rolname = @Name;
                """,
                new { principal.Name, identity.CurrentUser, identity.SessionUser },
                transaction,
                cancellationToken: cancellationToken));

            if (guard is null)
            {
                if (principal.Password is null && principal.EntraObjectId is null)
                    throw new InvalidOperationException(
                        $"Database:RuntimePrincipals entry '{principal.Name}' has no credentials and requires an existing PostgreSQL role (grant-only mode).");
                existing.Add(principal.Name, false);
                continue;
            }

            if (guard.IsPrivileged)
                throw new InvalidOperationException(
                    $"Database:RuntimePrincipals entry '{principal.Name}' targets a privileged or current PostgreSQL principal.");

            if (principal.EntraObjectId is Guid expectedObjectId)
            {
                EntraPrincipal? entraPrincipal = await connection.QuerySingleOrDefaultAsync<EntraPrincipal>(new CommandDefinition(
                    "SELECT rolename AS Name, objectid AS ObjectId FROM pg_catalog.pgaadauth_list_principals(false) WHERE rolename = @Name;",
                    new { principal.Name },
                    transaction,
                    cancellationToken: cancellationToken));
                if (entraPrincipal is null
                    || !Guid.TryParse(entraPrincipal.ObjectId, out Guid actualObjectId)
                    || actualObjectId != expectedObjectId)
                    throw new InvalidOperationException(
                        $"Database:RuntimePrincipals entry '{principal.Name}' targets a PostgreSQL role bound to a different identity.");
            }

            existing.Add(principal.Name, true);
        }

        foreach (RuntimePrincipal principal in principals)
        {
            string role = QuoteIdentifier(principal.Name);
            if (principal.Password is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "SELECT set_config('integrios.runtime_principal_password', @Password, true);",
                    new { principal.Password },
                    transaction,
                    cancellationToken: cancellationToken));
                string operation = existing[principal.Name] ? "ALTER ROLE" : "CREATE ROLE";
                try
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"""
                        DO $grant$
                        BEGIN
                            EXECUTE format(
                                '{operation} %I WITH LOGIN PASSWORD %L',
                                '{principal.Name}',
                                current_setting('integrios.runtime_principal_password'));
                        END
                        $grant$;
                        """,
                        transaction: transaction,
                        cancellationToken: cancellationToken));
                }
                catch (DbException)
                {
                    throw new InvalidOperationException(
                        $"Database:RuntimePrincipals entry '{principal.Name}' could not create or update its PostgreSQL principal. Create it externally and omit credential keys to use grant-only mode.");
                }
                // The supplied password stays in transaction-local state and out of SQL text.
            }
            else if (principal.EntraObjectId is Guid objectId && !existing[principal.Name])
            {
                try
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "SELECT pgaadauth_create_principal_with_oid(@Name, @ObjectId, false, false);",
                        new { principal.Name, ObjectId = objectId.ToString("D") },
                        transaction,
                        cancellationToken: cancellationToken));
                }
                catch (DbException)
                {
                    throw new InvalidOperationException(
                        $"Database:RuntimePrincipals entry '{principal.Name}' could not create its PostgreSQL Entra principal. Create it externally and omit credential keys to use grant-only mode.");
                }
            }

            await ConvergePermissionsAsync(connection, transaction, identity, principal, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ConvergePermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CurrentIdentity identity,
        RuntimePrincipal principal,
        CancellationToken cancellationToken)
    {
        string role = QuoteIdentifier(principal.Name);
        string database = QuoteIdentifier(identity.DatabaseName);
        string owner = QuoteIdentifier(identity.CurrentUser);

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            REVOKE ALL PRIVILEGES ON DATABASE {database} FROM {role};
            GRANT CONNECT ON DATABASE {database} TO {role};
            REVOKE ALL PRIVILEGES ON SCHEMA public FROM {role};
            GRANT USAGE ON SCHEMA public TO {role};
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM {role};
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM {role};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {role};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public REVOKE ALL ON TABLES FROM {role};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {role};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public REVOKE ALL ON SEQUENCES FROM {role};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {role};
            """,
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (principal.Scope == RuntimePrincipalScope.DataPlane)
        {
            foreach (string table in RuntimePrincipalScopes.OperatorAuthenticationTables)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    $"REVOKE ALL PRIVILEGES ON TABLE public.{QuoteIdentifier(table)} FROM {role};",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            }
        }

        bool canCreateSchemaObjects = await connection.QuerySingleAsync<bool>(new CommandDefinition(
            "SELECT has_schema_privilege(@Name, 'public', 'CREATE') OR has_database_privilege(@Name, current_database(), 'CREATE');",
            new { principal.Name },
            transaction,
            cancellationToken: cancellationToken));
        if (canCreateSchemaObjects)
            throw new InvalidOperationException(
                $"Database:RuntimePrincipals entry '{principal.Name}' retains inherited PostgreSQL schema-changing authority.");

        if (principal.Scope == RuntimePrincipalScope.DataPlane)
        {
            string checks = string.Join(" OR ", RuntimePrincipalScopes.OperatorAuthenticationTables.Select(
                table => $"has_table_privilege(@Name, 'public.{table}', 'SELECT') OR has_table_privilege(@Name, 'public.{table}', 'INSERT') OR has_table_privilege(@Name, 'public.{table}', 'UPDATE') OR has_table_privilege(@Name, 'public.{table}', 'DELETE')"));
            bool canAccessOperatorCredentials = await connection.QuerySingleAsync<bool>(new CommandDefinition(
                $"SELECT {checks};",
                new { principal.Name },
                transaction,
                cancellationToken: cancellationToken));
            if (canAccessOperatorCredentials)
                throw new InvalidOperationException(
                    $"Database:RuntimePrincipals entry '{principal.Name}' retains inherited access to Operator authentication tables.");
        }
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record CurrentIdentity(string DatabaseName, string CurrentUser, string SessionUser);

    private sealed record EntraPrincipal(string Name, string ObjectId);

    private sealed record TargetGuard(string Name, bool IsPrivileged);
}
