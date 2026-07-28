using static Integrios.QualificationTests.DatabaseLifecycleAssertions;

namespace Integrios.QualificationTests;

[Trait("Category", "Qualification")]
public sealed class MigrationGuardLifecycleTests(DatabaseLifecycleFixture fixture)
    : IClassFixture<DatabaseLifecycleFixture>
{
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
}
