using static Integrios.QualificationTests.DatabaseLifecycleAssertions;

namespace Integrios.QualificationTests;

[Trait("Category", "Qualification")]
[Trait("Tier", "database")]
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

    [Fact]
    public async Task V25_CrossTenantSubscriptionFailsWithRemediationAndLeavesDataUnchanged()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 24);
        await ExecuteAsync(
            database,
            """
            INSERT INTO integrations (id, key, name, direction, status)
            VALUES ('25000000-0000-0000-0000-000000000001', 'v25_webhook', 'V25 Webhook', 'both', 'active');

            INSERT INTO tenants (id, slug, name, status)
            VALUES
                ('25000000-0000-0000-0000-000000000002', 'v25-topic-tenant', 'V25 Topic Tenant', 'active'),
                ('25000000-0000-0000-0000-000000000003', 'v25-connection-tenant', 'V25 Connection Tenant', 'active');

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES (
                '25000000-0000-0000-0000-000000000004',
                '25000000-0000-0000-0000-000000000003',
                '25000000-0000-0000-0000-000000000001',
                'v25-destination',
                '{"url":"https://example.invalid/v25"}',
                'active');

            INSERT INTO topics (id, tenant_id, name, status)
            VALUES (
                '25000000-0000-0000-0000-000000000005',
                '25000000-0000-0000-0000-000000000002',
                'v25-topic',
                'active');

            INSERT INTO subscriptions (id, topic_id, name, match_rules, destination_connection_id, status)
            VALUES (
                '25000000-0000-0000-0000-000000000006',
                '25000000-0000-0000-0000-000000000005',
                'v25-cross-tenant',
                '{"event_type":"v25.test"}',
                '25000000-0000-0000-0000-000000000004',
                'active');
            """);

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            fixture.ExecuteMigrationSqlAsync(database, "V25__enforce_subscription_destination_tenant.sql"));

        Assert.Contains("Subscription references a Connection from another Tenant", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync(
            database,
            "subscriptions",
            "id = '25000000-0000-0000-0000-000000000006'"));
        Assert.Equal(0L, await CountColumnsAsync(database, "subscriptions", "tenant_id"));
    }

    [Theory]
    [InlineData("both", "used as both a source and a destination")]
    [InlineData("source", "reinterpret legacy destination authentication as source verification")]
    [InlineData("unused", "cannot infer a use for unused legacy Connection auth")]
    [InlineData("disabled_destination", "cannot infer a use for unused legacy Connection auth")]
    [InlineData("malformed_destination", "malformed or unsupported legacy destination authentication")]
    public async Task V27_AmbiguousLegacyAuthenticationFailsWithRepairGuidance(
        string use,
        string expectedMessage)
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 26);
        await ExecuteAsync(database, V27GuardGraphSql);

        if (use is "source" or "both")
        {
            await ExecuteAsync(database,
                "INSERT INTO topic_sources (tenant_id, topic_id, connection_id) VALUES ('27100000-0000-0000-0000-000000000001', '27100000-0000-0000-0000-000000000004', '27100000-0000-0000-0000-000000000003')");
        }
        if (use is "both" or "malformed_destination" or "disabled_destination")
        {
            await ExecuteAsync(database,
                $"INSERT INTO subscriptions (id, tenant_id, topic_id, name, match_rules, destination_connection_id, status) VALUES ('27100000-0000-0000-0000-000000000005', '27100000-0000-0000-0000-000000000001', '27100000-0000-0000-0000-000000000004', 'v27-subscription', '{{}}', '27100000-0000-0000-0000-000000000003', '{(use == "disabled_destination" ? "disabled" : "active")}')");
        }
        if (use == "malformed_destination")
        {
            await ExecuteAsync(database,
                "UPDATE connections SET auth = '{\"scheme\":\"bearer_token\",\"secret_refs\":{\"token\":\"guard_token\"}}'::jsonb WHERE id = '27100000-0000-0000-0000-000000000003'");
        }

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            fixture.ExecuteMigrationSqlAsync(database, "V27__directional_connection_credentials.sql"));

        Assert.Contains(expectedMessage, exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(1L, await CountColumnsAsync(database, "connections", "auth"));
        Assert.Equal(0L, await CountColumnsAsync(database, "connections", "source_verification"));
    }

    [Theory]
    [InlineData("unexpected_id", "unexpected id")]
    [InlineData("well_known_id_drift", "well-known webhook Integration id assigned to an unexpected contract")]
    [InlineData("schema_drift", "destination schema has drifted")]
    public async Task V28_RejectsUnexpectedWebhookV1Representation(
        string scenario,
        string expectedMessage)
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 27);
        string id = scenario == "unexpected_id"
            ? "28000000-0000-0000-0000-000000000001"
            : "00000000-0000-0000-0000-000000000001";
        string key = scenario == "well_known_id_drift" ? "not_webhook" : "webhook";
        string destinationSchema = scenario == "schema_drift"
            ? "{\"type\":\"object\",\"properties\":{\"endpoint\":{\"type\":\"string\"}},\"additionalProperties\":true}"
            : "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true}";
        string insert = """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest)
            VALUES (
                'WEBHOOK_ID', 'WEBHOOK_KEY', 1, 1, 'Webhook', 'both', '[]'::jsonb, 'active',
                '{"manifest_schema_version":1,"key":"WEBHOOK_KEY","contract_version":1,"direction":"both","source_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"destination_configuration_schema":DESTINATION_SCHEMA,"source_verification_schemes":[],"destination_authentication_schemes":[],"presentation":{"name":"Webhook","event_types":[],"authoring_presets":[]}}'::jsonb);
            """
            .Replace("WEBHOOK_ID", id, StringComparison.Ordinal)
            .Replace("WEBHOOK_KEY", key, StringComparison.Ordinal)
            .Replace("DESTINATION_SCHEMA", destinationSchema, StringComparison.Ordinal);
        await ExecuteAsync(database, insert);

        Npgsql.PostgresException exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            fixture.ExecuteMigrationSqlAsync(database, "V28__repair_webhook_v1_destination_schema.sql"));

        Assert.Contains(expectedMessage, exception.MessageText, StringComparison.Ordinal);
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

    private const string V27GuardGraphSql =
        """
        INSERT INTO tenants (id, slug, name, status)
        VALUES ('27100000-0000-0000-0000-000000000001', 'v27-guard', 'V27 Guard', 'active');

        INSERT INTO integrations (
            id, key, contract_version, manifest_schema_version, name, direction,
            supported_auth_schemes, status, manifest)
        VALUES (
            '27100000-0000-0000-0000-000000000002', 'v27_guard', 1, 1,
            'V27 Guard', 'both', '["bearer_token"]'::jsonb, 'active',
            '{"manifest_schema_version":1,"key":"v27_guard","contract_version":1,"direction":"both","source_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"destination_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"source_verification_schemes":[],"destination_authentication_schemes":[{"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}],"presentation":{"name":"V27 Guard","event_types":[],"authoring_presets":[]}}'::jsonb);

        INSERT INTO connections (id, tenant_id, integration_id, name, config, auth, status)
        VALUES (
            '27100000-0000-0000-0000-000000000003', '27100000-0000-0000-0000-000000000001',
            '27100000-0000-0000-0000-000000000002', 'v27-guard', '{}',
            '{"scheme":"bearer_token","config":{},"secret_refs":{"token":"guard_token"}}', 'active');

        INSERT INTO topics (id, tenant_id, name, status)
        VALUES ('27100000-0000-0000-0000-000000000004', '27100000-0000-0000-0000-000000000001', 'v27-guard-topic', 'active');
        """;
}
