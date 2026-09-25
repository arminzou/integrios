using Dapper;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Integrios.Infrastructure.Data;

internal static class SqlServerRuntimePrincipalGrants
{
    public static async Task ApplyAsync(
        string connectionString,
        IReadOnlyList<RuntimePrincipal> principals,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (RuntimePrincipal principal in principals)
        {
            TargetGuard? guard = await connection.QuerySingleOrDefaultAsync<TargetGuard>(new CommandDefinition(
                """
                ;WITH member_roles AS
                (
                    SELECT drm.role_principal_id
                    FROM sys.database_role_members drm
                    WHERE drm.member_principal_id = USER_ID(@Name)
                    UNION ALL
                    SELECT drm.role_principal_id
                    FROM sys.database_role_members drm
                    INNER JOIN member_roles mr ON drm.member_principal_id = mr.role_principal_id
                )
                SELECT dp.name AS Name,
                       dp.type AS Type,
                       CASE WHEN @ClientId IS NOT NULL
                                  AND (dp.type <> 'E' OR dp.sid IS NULL
                                    OR dp.sid <> CONVERT(varbinary(16), @ClientId))
                            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsIdentityMismatch,
                       CASE WHEN dp.type = 'R'
                                  OR dp.name IN (USER_NAME(), SUSER_SNAME(), ORIGINAL_LOGIN(), 'dbo')
                                  OR dp.sid = (SELECT owner_sid FROM sys.databases WHERE name = DB_NAME())
                                  OR IS_SRVROLEMEMBER('sysadmin', dp.name) = 1
                                  OR IS_SRVROLEMEMBER('serveradmin', dp.name) = 1
                                  OR IS_SRVROLEMEMBER('securityadmin', dp.name) = 1
                                  OR EXISTS
                                  (
                                      SELECT 1
                                      FROM member_roles mr
                                      INNER JOIN sys.database_principals role_principal
                                          ON role_principal.principal_id = mr.role_principal_id
                                      WHERE role_principal.name IN ('db_owner', 'db_ddladmin', 'db_securityadmin', 'db_accessadmin')
                                  )
                                  OR EXISTS
                                  (
                                      SELECT 1
                                      FROM sys.database_permissions p
                                      LEFT JOIN sys.schemas s ON p.class = 3 AND s.schema_id = p.major_id
                                      LEFT JOIN sys.objects o ON p.class = 1 AND o.object_id = p.major_id
                                      WHERE (p.grantee_principal_id IN (SELECT role_principal_id FROM member_roles)
                                          OR p.grantee_principal_id = 0)
                                        AND p.state IN ('G', 'W')
                                        AND ((p.class = 0 AND p.permission_name IN ('CREATE TABLE', 'ALTER ANY SCHEMA', 'CONTROL'))
                                          OR (p.class = 3 AND s.name = 'dbo' AND p.permission_name IN ('ALTER', 'CONTROL', 'TAKE OWNERSHIP'))
                                          OR (p.class = 1 AND o.schema_id = SCHEMA_ID('dbo') AND p.permission_name IN ('ALTER', 'CONTROL', 'TAKE OWNERSHIP')))
                                  )
                            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsPrivileged
                FROM sys.database_principals dp
                WHERE dp.name = @Name
                OPTION (MAXRECURSION 100);
                """,
                new { principal.Name, ClientId = principal.EntraClientId },
                transaction,
                cancellationToken: cancellationToken));

            if (guard is null)
            {
                if (principal.Password is null && principal.EntraClientId is null)
                    throw new InvalidOperationException(
                        $"Database:RuntimePrincipals entry '{principal.Name}' has no credentials and requires an existing SQL Server principal (grant-only mode).");
                existing.Add(principal.Name, false);
                continue;
            }

            if (guard.IsPrivileged)
                throw new InvalidOperationException(
                    $"Database:RuntimePrincipals entry '{principal.Name}' targets a privileged, database-role, or current SQL Server principal.");
            if (guard.IsIdentityMismatch)
                throw new InvalidOperationException(
                    $"Database:RuntimePrincipals entry '{principal.Name}' targets a SQL Server principal bound to a different identity.");

            existing.Add(principal.Name, true);
        }

        foreach (RuntimePrincipal principal in principals)
        {
            try
            {
                if (principal.Password is not null)
                    await SetPasswordAsync(connection, transaction, principal, existing[principal.Name], cancellationToken);
                else if (principal.EntraClientId is not null && !existing[principal.Name])
                    await CreateEntraPrincipalAsync(connection, transaction, principal, cancellationToken);
            }
            catch (DbException)
            {
                throw new InvalidOperationException(
                    $"Database:RuntimePrincipals entry '{principal.Name}' could not create or update its SQL Server principal. Create it externally and omit credential keys to use grant-only mode.");
            }

            await ConvergePermissionsAsync(connection, transaction, principal, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SetPasswordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        RuntimePrincipal principal,
        bool exists,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Name", principal.Name);
        parameters.Add("Password", principal.Password);
        string verb = exists ? "ALTER" : "CREATE";
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            DECLARE @statement nvarchar(max) = N'{verb} USER ' + QUOTENAME(@Name)
                + N' WITH PASSWORD = ' + QUOTENAME(@Password, '''');
            EXEC sys.sp_executesql @statement;
            """,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Task CreateEntraPrincipalAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        RuntimePrincipal principal,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            $"""
            -- For service principals, Azure SQL stores the application client ID as the SID. This
            -- form avoids a directory lookup by the server identity.
            DECLARE @sid varbinary(16) = CONVERT(varbinary(16), @ClientId);
            DECLARE @statement nvarchar(max) = N'CREATE USER ' + QUOTENAME(@Name)
                + N' WITH SID = ' + CONVERT(varchar(34), @sid, 1) + N', TYPE = E';
            EXEC sys.sp_executesql @statement;
            """,
            new { Name = principal.Name, ClientId = principal.EntraClientId },
            transaction,
            cancellationToken: cancellationToken));

    private static async Task ConvergePermissionsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        RuntimePrincipal principal,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Name", principal.Name);
        parameters.Add("SchemaId", "dbo");
        string quotedPrincipal = QuoteIdentifier(principal.Name);
        IEnumerable<DirectPermission> permissions = await connection.QueryAsync<DirectPermission>(new CommandDefinition(
            $"""
            SELECT p.permission_name AS PermissionName,
                   p.state AS State,
                   p.class AS Class,
                   COALESCE(s.name, os.name) AS SchemaName,
                   o.name AS ObjectName,
                   c.name AS ColumnName
            FROM sys.database_permissions p
            LEFT JOIN sys.schemas s ON p.class = 3 AND s.schema_id = p.major_id
            LEFT JOIN sys.objects o ON p.class = 1 AND o.object_id = p.major_id
            LEFT JOIN sys.schemas os ON o.schema_id = os.schema_id
            LEFT JOIN sys.columns c ON c.object_id = p.major_id AND c.column_id = p.minor_id
            WHERE p.grantee_principal_id = USER_ID(@Name)
              AND (p.class = 0 OR (p.class = 3 AND s.name = @SchemaId) OR (p.class = 1 AND os.name = @SchemaId));
            """,
            parameters,
            transaction,
            cancellationToken: cancellationToken));

        foreach (DirectPermission permission in permissions)
        {
            string securable = permission.Class switch
            {
                0 => $"DATABASE::{QuoteIdentifier(connection.Database)}",
                3 => $"SCHEMA::{QuoteIdentifier(permission.SchemaName!)}",
                1 => $"OBJECT::{QuoteIdentifier(permission.SchemaName!)}.{QuoteIdentifier(permission.ObjectName!)}"
                    + (permission.ColumnName is null ? string.Empty : $" ({QuoteIdentifier(permission.ColumnName)})"),
                _ => throw new InvalidOperationException("A runtime principal permission has an unsupported securable class."),
            };
            await connection.ExecuteAsync(new CommandDefinition(
                // A permission held WITH GRANT OPTION can only be revoked with CASCADE.
                $"REVOKE {permission.PermissionName} ON {securable} FROM {quotedPrincipal}{(permission.State == "W" ? " CASCADE" : string.Empty)};",
                transaction: transaction,
                cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"GRANT CONNECT TO {quotedPrincipal}; GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO {quotedPrincipal};",
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (principal.Scope == RuntimePrincipalScope.DataPlane)
        {
            foreach (string table in RuntimePrincipalScopes.OperatorAuthenticationTables)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    $"DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.{QuoteIdentifier(table)} TO {QuoteIdentifier(principal.Name)};",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            }
        }

    }

    private static string QuoteIdentifier(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private sealed record TargetGuard(string Name, string Type, bool IsIdentityMismatch, bool IsPrivileged);

    private sealed record DirectPermission(
        string PermissionName,
        string State,
        byte Class,
        string? SchemaName,
        string? ObjectName,
        string? ColumnName);
}
