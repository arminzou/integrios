using System.Security.Cryptography;
using System.Text;

namespace Integrios.QualificationTests;

[Trait("Category", "Qualification")]
public sealed class DatabaseLifecycleTests(DatabaseLifecycleFixture fixture)
    : IClassFixture<DatabaseLifecycleFixture>
{
    [Fact]
    public async Task FreshDatabase_RepeatedFlywayAndProductionBootstrap_AreSafeAndIdempotent()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();

        string firstMigrate = await fixture.RunFlywayAsync(database, "migrate");
        string secondMigrate = await fixture.RunFlywayAsync(database, "migrate");
        string validate = await fixture.RunFlywayAsync(database, "validate");

        Assert.Contains("Successfully applied", firstMigrate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("up to date", secondMigrate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Successfully validated", validate, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(22L, await DatabaseLifecycleFixture.ScalarAsync<long>(
            database, "SELECT COUNT(*) FROM flyway_schema_history WHERE success"));
        Assert.Equal("text|YES", await ColumnShapeAsync(database, "subscription_deliveries", "destination_url"));
        Assert.Equal("text|NO", await ColumnShapeAsync(database, "subscription_deliveries", "integration_key"));
        Assert.Equal("jsonb|YES", await ColumnShapeAsync(database, "subscription_deliveries", "destination_auth"));
        Assert.Equal("uuid|YES", await ColumnShapeAsync(database, "subscription_deliveries", "active_attempt_id"));
        Assert.Equal("timestamp with time zone|YES", await ColumnShapeAsync(database, "subscription_deliveries", "lease_expires_at"));
        Assert.Equal("uuid|NO", await ColumnShapeAsync(database, "delivery_attempts", "subscription_delivery_id"));
        Assert.Equal("text|YES", await ColumnShapeAsync(database, "delivery_attempts", "failure_phase"));
        Assert.Equal(0L, await CountColumnsAsync(database, "subscriptions", "delivery_policy", "dlq_enabled"));
        Assert.Equal(0L, await CountAsync(database, "integrations"));
        Assert.Equal(0L, await CountAsync(database, "admin_keys"));

        BootstrapProcessResult missingSecret =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, secret: null);

        Assert.NotEqual(0, missingSecret.ExitCode);
        Assert.Contains("requires a non-empty", missingSecret.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shown once", missingSecret.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await CountAsync(database, "integrations"));
        Assert.Equal(0L, await CountAsync(database, "admin_keys"));

        const string suppliedSecret = "qualification-production-secret";
        BootstrapProcessResult firstBootstrap =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, suppliedSecret);

        Assert.Equal(0, firstBootstrap.ExitCode);
        Assert.DoesNotContain(suppliedSecret, firstBootstrap.Output, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync(database, "integrations", "key = 'webhook'"));
        Assert.Equal(1L, await CountAsync(database, "admin_keys", "tenant_id IS NULL AND revoked_at IS NULL"));

        await ExecuteAsync(database, "UPDATE integrations SET name = 'Drifted', status = 'disabled' WHERE key = 'webhook'");

        BootstrapProcessResult secondBootstrap =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, "unused-second-secret");

        Assert.Equal(0, secondBootstrap.ExitCode);
        Assert.Contains("no-op", secondBootstrap.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unused-second-secret", secondBootstrap.Output, StringComparison.Ordinal);
        Assert.Equal("Webhook|both|active|[]", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT name || '|' || direction || '|' || status || '|' || supported_auth_schemes::text FROM integrations WHERE key = 'webhook'"));
        Assert.Equal(Hash(suppliedSecret), await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT secret_hash FROM admin_keys WHERE tenant_id IS NULL AND revoked_at IS NULL"));
    }

    [Fact]
    public async Task PopulatedV17Database_UpgradesAndReconcilesReferencedBuiltin()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 17);
        await DatabaseLifecycleFixture.ExecuteFixtureAsync(database, "V17_used_database.sql");

        await fixture.RunFlywayAsync(database, "migrate");
        await fixture.RunFlywayAsync(database, "validate");

        Assert.Equal(1L, await CountAsync(
            database,
            "connections",
            "id = '17000000-0000-0000-0000-000000000002' AND integration_id = '00000000-0000-0000-0000-000000000001'"));
        Assert.Equal("Drifted Webhook|destination|disabled", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT name || '|' || direction || '|' || status FROM integrations WHERE key = 'webhook'"));

        BootstrapProcessResult bootstrap = await DatabaseLifecycleFixture.RunProductionBootstrapAsync(
            database,
            "v17-upgrade-secret");

        Assert.Equal(0, bootstrap.ExitCode);
        Assert.DoesNotContain("v17-upgrade-secret", bootstrap.Output, StringComparison.Ordinal);
        Assert.Equal("Webhook|both|active|[]", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT name || '|' || direction || '|' || status || '|' || supported_auth_schemes::text FROM integrations WHERE key = 'webhook'"));
        Assert.Equal(1L, await CountAsync(
            database,
            "connections",
            "id = '17000000-0000-0000-0000-000000000002'"));
    }

    [Fact]
    public async Task PopulatedV18Database_UpgradePreservesOperatorKeyAndReferencedBuiltin()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 18);
        await DatabaseLifecycleFixture.ExecuteFixtureAsync(database, "V18_used_database.sql");

        await fixture.RunFlywayAsync(database, "migrate");
        await fixture.RunFlywayAsync(database, "validate");

        Assert.Equal("sha256:1111111111111111111111111111111111111111111111111111111111111111", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT secret_hash FROM admin_keys WHERE tenant_id IS NULL AND public_key = 'global_admin_key'"));
        Assert.Equal(1L, await CountAsync(
            database,
            "connections",
            "id = '18000000-0000-0000-0000-000000000002' AND integration_id = '00000000-0000-0000-0000-000000000001'"));

        BootstrapProcessResult bootstrap = await DatabaseLifecycleFixture.RunProductionBootstrapAsync(
            database,
            "must-not-replace-operator-secret");

        Assert.Equal(0, bootstrap.ExitCode);
        Assert.Contains("no-op", bootstrap.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-replace-operator-secret", bootstrap.Output, StringComparison.Ordinal);
        Assert.Equal("sha256:1111111111111111111111111111111111111111111111111111111111111111", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT secret_hash FROM admin_keys WHERE tenant_id IS NULL AND public_key = 'global_admin_key'"));
        Assert.Equal("Webhook|both|active|[]", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT name || '|' || direction || '|' || status || '|' || supported_auth_schemes::text FROM integrations WHERE key = 'webhook'"));
    }

    [Fact]
    public async Task V20_WithExistingSubscriptionDelivery_FailsWithClearMessage()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 19);
        await ExecuteAsync(
            database,
            """
            INSERT INTO integrations (id, key, name, direction, status)
            VALUES ('20000000-0000-0000-0000-000000000001', 'v20_webhook', 'V20 Webhook', 'both', 'active');

            INSERT INTO tenants (id, slug, name, status)
            VALUES ('20000000-0000-0000-0000-000000000002', 'v20-tenant', 'V20 Tenant', 'active');

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES (
                '20000000-0000-0000-0000-000000000003',
                '20000000-0000-0000-0000-000000000002',
                '20000000-0000-0000-0000-000000000001',
                'v20-destination',
                '{"url":"https://example.invalid/v20"}',
                'active');

            INSERT INTO topics (id, tenant_id, name, status)
            VALUES (
                '20000000-0000-0000-0000-000000000004',
                '20000000-0000-0000-0000-000000000002',
                'v20-topic',
                'active');

            INSERT INTO subscriptions (id, topic_id, name, match_rules, destination_connection_id, status)
            VALUES (
                '20000000-0000-0000-0000-000000000005',
                '20000000-0000-0000-0000-000000000004',
                'v20-subscription',
                '{"event_type":"v20.test"}',
                '20000000-0000-0000-0000-000000000003',
                'active');

            INSERT INTO events (id, tenant_id, topic_id, event_type, payload, status, accepted_at)
            VALUES (
                '20000000-0000-0000-0000-000000000006',
                '20000000-0000-0000-0000-000000000002',
                '20000000-0000-0000-0000-000000000004',
                'v20.test',
                '{}',
                'fanned_out',
                now());

            INSERT INTO subscription_deliveries (event_id, subscription_id, destination_connection_id)
            VALUES (
                '20000000-0000-0000-0000-000000000006',
                '20000000-0000-0000-0000-000000000005',
                '20000000-0000-0000-0000-000000000003');
            """);

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            fixture.ExecuteMigrationSqlAsync(database, "V20__snapshot_subscription_delivery_execution.sql"));

        Assert.Contains("requires subscription_deliveries to be empty", exception.MessageText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task V21_WithExistingDeliveryState_FailsWithClearMessage(
        bool insertSubscriptionDelivery,
        bool insertDeliveryAttempt)
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 20);
        await ExecuteAsync(database, V21GraphSql);

        if (insertSubscriptionDelivery)
        {
            await ExecuteAsync(
                database,
                """
                INSERT INTO subscription_deliveries (
                    event_id, subscription_id, destination_connection_id, destination_url, integration_key)
                VALUES (
                    '21000000-0000-0000-0000-000000000006',
                    '21000000-0000-0000-0000-000000000005',
                    '21000000-0000-0000-0000-000000000003',
                    'https://example.invalid/v21',
                    'v21_webhook');
                """);
        }

        if (insertDeliveryAttempt)
        {
            await ExecuteAsync(
                database,
                """
                INSERT INTO delivery_attempts (
                    event_id, subscription_id, destination_connection_id, attempt_number, status)
                VALUES (
                    '21000000-0000-0000-0000-000000000006',
                    '21000000-0000-0000-0000-000000000005',
                    '21000000-0000-0000-0000-000000000003',
                    1,
                    'failed');
                """);
        }

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            fixture.ExecuteMigrationSqlAsync(database, "V21__fence_subscription_delivery_attempts.sql"));

        Assert.Contains(
            "requires subscription_deliveries and delivery_attempts to be empty",
            exception.MessageText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task V22_InvalidExistingSlugFailsWithRemediationAndLeavesDataUnchanged()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 21);
        await ExecuteAsync(
            database,
            """
            INSERT INTO tenants (id, slug, name, status)
            VALUES ('22000000-0000-0000-0000-000000000001', 'Invalid_Slug', 'Invalid Tenant', 'active');
            """);

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            fixture.ExecuteMigrationSqlAsync(database, "V22__enforce_tenant_slug_contract.sql"));

        Assert.Contains("lowercase DNS-label tenant slugs", exception.MessageText, StringComparison.Ordinal);
        Assert.Contains("move or map its external secret namespace", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal("Invalid_Slug", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT slug FROM tenants WHERE id = '22000000-0000-0000-0000-000000000001'"));
        Assert.Equal(0L, await DatabaseLifecycleFixture.ScalarAsync<long>(
            database, "SELECT COUNT(*) FROM pg_constraint WHERE conname = 'chk_tenants_slug_dns_label'"));
    }

    [Fact]
    public async Task V22_DatabaseConstraintRejectsInvalidDirectInsert()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate");

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(
            database,
            """
            INSERT INTO tenants (id, slug, name, status)
            VALUES ('22000000-0000-0000-0000-000000000002', '-invalid', 'Invalid Tenant', 'active');
            """));

        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("chk_tenants_slug_dns_label", exception.ConstraintName);
    }

    private const string V21GraphSql =
        """
        INSERT INTO integrations (id, key, name, direction, status)
        VALUES ('21000000-0000-0000-0000-000000000001', 'v21_webhook', 'V21 Webhook', 'both', 'active');

        INSERT INTO tenants (id, slug, name, status)
        VALUES ('21000000-0000-0000-0000-000000000002', 'v21-tenant', 'V21 Tenant', 'active');

        INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
        VALUES (
            '21000000-0000-0000-0000-000000000003',
            '21000000-0000-0000-0000-000000000002',
            '21000000-0000-0000-0000-000000000001',
            'v21-destination',
            '{"url":"https://example.invalid/v21"}',
            'active');

        INSERT INTO topics (id, tenant_id, name, status)
        VALUES (
            '21000000-0000-0000-0000-000000000004',
            '21000000-0000-0000-0000-000000000002',
            'v21-topic',
            'active');

        INSERT INTO subscriptions (id, topic_id, name, match_rules, destination_connection_id, status)
        VALUES (
            '21000000-0000-0000-0000-000000000005',
            '21000000-0000-0000-0000-000000000004',
            'v21-subscription',
            '{"event_type":"v21.test"}',
            '21000000-0000-0000-0000-000000000003',
            'active');

        INSERT INTO events (id, tenant_id, topic_id, event_type, payload, status, accepted_at)
        VALUES (
            '21000000-0000-0000-0000-000000000006',
            '21000000-0000-0000-0000-000000000002',
            '21000000-0000-0000-0000-000000000004',
            'v21.test',
            '{}',
            'fanned_out',
            now());
        """;

    private static async Task ExecuteAsync(QualificationDatabase database, string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<long> CountAsync(
        QualificationDatabase database,
        string table,
        string where = "TRUE") =>
        DatabaseLifecycleFixture.ScalarAsync<long>(database, $"SELECT COUNT(*) FROM {table} WHERE {where}");

    private static Task<string> ColumnShapeAsync(
        QualificationDatabase database,
        string table,
        string column) =>
        DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            $"SELECT data_type || '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'");

    private static Task<long> CountColumnsAsync(
        QualificationDatabase database,
        string table,
        params string[] columns)
    {
        string names = string.Join(", ", columns.Select(column => $"'{column}'"));
        return DatabaseLifecycleFixture.ScalarAsync<long>(
            database,
            $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name IN ({names})");
    }

    private static string Hash(string secret) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
