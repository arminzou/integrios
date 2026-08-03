using static Integrios.QualificationTests.DatabaseLifecycleAssertions;

namespace Integrios.QualificationTests;

[Trait("Category", "Qualification")]
[Trait("Tier", "database")]
public sealed class SchemaContractLifecycleTests(DatabaseLifecycleFixture fixture)
    : IClassFixture<DatabaseLifecycleFixture>
{
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

    [Fact]
    public async Task V23_PreservesHistoricalNullSourceAndRequiresValidSourceForNewEvents()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 22);
        await ExecuteAsync(
            database,
            """
            INSERT INTO tenants (id, slug, name, status)
            VALUES ('23000000-0000-0000-0000-000000000001', 'v23-tenant', 'V23 Tenant', 'active');

            INSERT INTO integrations (
                id, key, name, direction, supported_auth_schemes, status
            ) VALUES (
                '23000000-0000-0000-0000-000000000002', 'v23_source', 'V23 Source',
                'source', '[]'::jsonb, 'active');

            INSERT INTO connections (
                id, tenant_id, integration_id, name, config, status
            ) VALUES (
                '23000000-0000-0000-0000-000000000003',
                '23000000-0000-0000-0000-000000000001',
                '23000000-0000-0000-0000-000000000002',
                'v23-source', '{}'::jsonb, 'active');

            INSERT INTO topics (id, tenant_id, name, status)
            VALUES (
                '23000000-0000-0000-0000-000000000004',
                '23000000-0000-0000-0000-000000000001',
                'v23-topic', 'active');

            INSERT INTO topic_sources (topic_id, connection_id)
            VALUES (
                '23000000-0000-0000-0000-000000000004',
                '23000000-0000-0000-0000-000000000003');

            INSERT INTO events (
                id, tenant_id, topic_id, source_connection_id, event_type, payload, status
            ) VALUES (
                '23000000-0000-0000-0000-000000000005',
                '23000000-0000-0000-0000-000000000001',
                '23000000-0000-0000-0000-000000000004',
                NULL, 'historical.event', '{}'::jsonb, 'accepted');
            """);

        await fixture.ExecuteMigrationSqlAsync(database, "V23__enforce_event_source_connection.sql");

        Assert.Equal(1L, await CountAsync(
            database,
            "events",
            "id = '23000000-0000-0000-0000-000000000005' AND source_connection_id IS NULL"));

        var missingSource = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(
            database,
            """
            INSERT INTO events (
                id, tenant_id, topic_id, source_connection_id, event_type, payload, status
            ) VALUES (
                '23000000-0000-0000-0000-000000000006',
                '23000000-0000-0000-0000-000000000001',
                '23000000-0000-0000-0000-000000000004',
                NULL, 'new.invalid', '{}'::jsonb, 'accepted');
            """));

        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, missingSource.SqlState);
        Assert.Equal("ck_events_source_connection_required", missingSource.ConstraintName);

        await ExecuteAsync(
            database,
            """
            INSERT INTO events (
                id, tenant_id, topic_id, source_connection_id, event_type, payload, status
            ) VALUES (
                '23000000-0000-0000-0000-000000000007',
                '23000000-0000-0000-0000-000000000001',
                '23000000-0000-0000-0000-000000000004',
                '23000000-0000-0000-0000-000000000003',
                'new.valid', '{}'::jsonb, 'accepted');
            """);

        Assert.Equal(1L, await CountAsync(
            database,
            "events",
            "id = '23000000-0000-0000-0000-000000000007'"));
    }

    [Fact]
    public async Task V24_RevokesLegacyTenantKeyBeforeRemovingUnsupportedCredentialScope()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 23);
        await ExecuteAsync(
            database,
            """
            INSERT INTO tenants (id, slug, name, status)
            VALUES ('24000000-0000-0000-0000-000000000001', 'v24-tenant', 'V24 Tenant', 'active');

            INSERT INTO admin_keys (id, tenant_id, public_key, secret_hash, name)
            VALUES
                ('24000000-0000-0000-0000-000000000002', NULL, 'v24_global', 'sha256:global', 'Global'),
                ('24000000-0000-0000-0000-000000000003',
                 '24000000-0000-0000-0000-000000000001', 'v24_tenant', 'sha256:tenant', 'Tenant');

            INSERT INTO api_keys (id, tenant_id, name, key_prefix, key_hash, scopes, status)
            VALUES (
                '24000000-0000-0000-0000-000000000004',
                '24000000-0000-0000-0000-000000000001',
                'v24-api-key', 'intg_v24', 'sha256:api', ARRAY['events.write'], 'active');
            """);

        await fixture.ExecuteMigrationSqlAsync(database, "V24__enforce_credential_authority.sql");

        Assert.Equal(1L, await CountAsync(database, "admin_keys", "public_key = 'v24_global' AND revoked_at IS NULL"));
        Assert.Equal(1L, await CountAsync(database, "admin_keys", "public_key = 'v24_tenant' AND revoked_at IS NOT NULL"));
        Assert.Equal(0L, await CountColumnsAsync(database, "admin_keys", "tenant_id"));
        Assert.Equal(0L, await CountColumnsAsync(database, "api_keys", "scopes"));
    }

    [Fact]
    public async Task V26_PreservesLegacyIntegrationIdentityAndMakesFunctionalVersionsImmutable()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 25);
        await ExecuteAsync(
            database,
            """
            INSERT INTO tenants (id, slug, name, status)
            VALUES ('26000000-0000-0000-0000-000000000001', 'v26-tenant', 'V26 Tenant', 'active');

            INSERT INTO integrations (
                id, key, name, direction, supported_auth_schemes, status, description
            ) VALUES (
                '26000000-0000-0000-0000-000000000002', 'v26_api', 'V26 API',
                'destination', '["bearer_token"]'::jsonb, 'active', 'Legacy destination');

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES (
                '26000000-0000-0000-0000-000000000003',
                '26000000-0000-0000-0000-000000000001',
                '26000000-0000-0000-0000-000000000002',
                'v26-destination', '{"url":"https://example.test"}'::jsonb, 'active');
            """);

        await fixture.ExecuteMigrationSqlAsync(database, "V26__version_integration_manifests.sql");

        Assert.Equal(1L, await CountAsync(
            database,
            "integrations",
            """
            id = '26000000-0000-0000-0000-000000000002'
            AND key = 'v26_api'
            AND contract_version = 1
            AND manifest_schema_version = 1
            AND manifest->>'direction' = 'destination'
            AND manifest->'destination_authentication_schemes'->0->>'scheme' = 'bearer_token'
            """));
        Assert.Equal(1L, await CountAsync(
            database,
            "connections",
            "integration_id = '26000000-0000-0000-0000-000000000002'"));
        Assert.Equal(2L, await DatabaseLifecycleFixture.ScalarAsync<long>(
            database,
            """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'integrations'
              AND column_name IN ('contract_version', 'manifest_schema_version')
              AND column_default IS NULL
            """));

        var immutable = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(
            database,
            """
            UPDATE integrations
            SET direction = 'both'
            WHERE id = '26000000-0000-0000-0000-000000000002';
            """));
        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, immutable.SqlState);

        var manifestImmutable = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(
            database,
            """
            UPDATE integrations
            SET manifest = jsonb_set(
                manifest,
                '{destination_configuration_schema,additionalProperties}',
                'false'::jsonb)
            WHERE id = '26000000-0000-0000-0000-000000000002';
            """));
        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, manifestImmutable.SqlState);

        await ExecuteAsync(
            database,
            """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, description, manifest)
            VALUES (
                '26000000-0000-0000-0000-000000000004', 'v26_api', 2, 1, 'V26 API v2',
                'destination', '[]'::jsonb, 'active', NULL,
                '{
                  "manifest_schema_version":1,
                  "key":"v26_api",
                  "contract_version":2,
                  "direction":"destination",
                  "destination_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},
                  "source_verification_schemes":[],
                  "destination_authentication_schemes":[],
                  "presentation":{"name":"V26 API v2","event_types":[],"authoring_presets":[]}
                }'::jsonb);
            """);

        Assert.Equal(2L, await CountAsync(database, "integrations", "key = 'v26_api'"));
    }

    [Fact]
    public async Task V26_RejectsSourceOnlyLegacyDestinationAuthenticationForRepair()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 25);
        await ExecuteAsync(
            database,
            """
            INSERT INTO integrations (id, key, name, direction, supported_auth_schemes, status)
            VALUES (
                '26000000-0000-0000-0000-000000000010', 'v26_invalid_source', 'Invalid Source',
                'source', '["bearer_token"]'::jsonb, 'active');
            """);

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            fixture.ExecuteMigrationSqlAsync(database, "V26__version_integration_manifests.sql"));

        Assert.Contains("source-only Integration", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(0L, await CountColumnsAsync(database, "integrations", "contract_version"));
    }

    [Theory]
    [InlineData("sideways", "[]", "unsupported direction")]
    [InlineData("destination", "[\"oauth_client_credentials\"]", "unsupported destination authentication scheme")]
    public async Task V26_RejectsUnsupportedLegacyFunctionalContractsForRepair(
        string direction,
        string authenticationSchemes,
        string expectedMessage)
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 25);
        await ExecuteAsync(
            database,
            $$"""
            INSERT INTO integrations (id, key, name, direction, supported_auth_schemes, status)
            VALUES (
                '26000000-0000-0000-0000-000000000011', 'v26_invalid_contract', 'Invalid Contract',
                '{{direction}}', '{{authenticationSchemes}}'::jsonb, 'active');
            """);

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            fixture.ExecuteMigrationSqlAsync(database, "V26__version_integration_manifests.sql"));

        Assert.Contains(expectedMessage, exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(0L, await CountColumnsAsync(database, "integrations", "contract_version"));
    }

    [Fact]
    public async Task V27_MigratesDestinationAuthenticationAndPreservesActiveUseDerivedConnections()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 26);
        await ExecuteAsync(database, V27GraphSql);

        await fixture.ExecuteMigrationSqlAsync(database, "V27__directional_connection_credentials.sql");

        Assert.Equal(0L, await CountColumnsAsync(database, "connections", "auth"));
        Assert.Equal(2L, await DatabaseLifecycleFixture.ScalarAsync<long>(database,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'connections' AND column_name IN ('source_verification', 'destination_authentication')"));
        Assert.Equal("bearer_token", await DatabaseLifecycleFixture.ScalarAsync<string>(database,
            "SELECT destination_authentication->>'scheme' FROM connections WHERE name = 'v27-destination'"));
        Assert.Equal(1L, await CountAsync(database, "connections",
            "name = 'v27-both-open' AND source_verification IS NULL AND destination_authentication IS NULL"));
    }

    [Fact]
    public async Task V28_RepairsOnlyTheWebhookV1DestinationSchema()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 27);
        await ExecuteAsync(database,
            """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest)
            VALUES (
                '00000000-0000-0000-0000-000000000001', 'webhook', 1, 1, 'Webhook', 'both',
                '[]'::jsonb, 'active',
                '{"manifest_schema_version":1,"key":"webhook","contract_version":1,"direction":"both","source_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"destination_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"source_verification_schemes":[],"destination_authentication_schemes":[],"presentation":{"name":"Webhook","event_types":[],"authoring_presets":[]}}'::jsonb);
            """);

        await fixture.ExecuteMigrationSqlAsync(database, "V28__repair_webhook_v1_destination_schema.sql");

        Assert.Equal(1, await DatabaseLifecycleFixture.ScalarAsync<int>(database,
            "SELECT contract_version FROM integrations WHERE key = 'webhook'"));
        Assert.Equal("uri", await DatabaseLifecycleFixture.ScalarAsync<string>(database,
            "SELECT manifest->'destination_configuration_schema'->'properties'->'url'->>'format' FROM integrations WHERE key = 'webhook'"));
        Assert.Equal("url", await DatabaseLifecycleFixture.ScalarAsync<string>(database,
            "SELECT manifest->'destination_configuration_schema'->'required'->>0 FROM integrations WHERE key = 'webhook'"));
        Assert.Equal(1L, await DatabaseLifecycleFixture.ScalarAsync<long>(database,
            "SELECT COUNT(*) FROM pg_trigger WHERE tgrelid = 'integrations'::regclass AND tgname = 'integrations_reject_functional_update' AND tgenabled = 'O'"));
    }

    [Fact]
    public async Task V29_WrapsExistingSchemeArraysWithExplicitPermissionFlags()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate", target: 28);
        await ExecuteAsync(database,
            """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest)
            VALUES
                ('29000000-0000-0000-0000-000000000001', 'v29_open', 1, 1, 'V29 Open', 'both',
                 '[]'::jsonb, 'active',
                 '{"manifest_schema_version":1,"key":"v29_open","contract_version":1,"direction":"both","source_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"destination_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"source_verification_schemes":[],"destination_authentication_schemes":[],"presentation":{"name":"V29 Open","event_types":[],"authoring_presets":[]}}'::jsonb),
                ('29000000-0000-0000-0000-000000000002', 'v29_authenticated', 1, 1, 'V29 Authenticated', 'destination',
                 '["bearer_token"]'::jsonb, 'active',
                 '{"manifest_schema_version":1,"key":"v29_authenticated","contract_version":1,"direction":"destination","destination_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"source_verification_schemes":[],"destination_authentication_schemes":[{"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}],"presentation":{"name":"V29 Authenticated","event_types":[],"authoring_presets":[]}}'::jsonb);
            """);

        await fixture.ExecuteMigrationSqlAsync(database, "V29__require_explicit_verification_permissions.sql");

        Assert.True(await DatabaseLifecycleFixture.ScalarAsync<bool>(database,
            "SELECT manifest->'source_verification'->>'allow_unverified' = 'true' FROM integrations WHERE key = 'v29_open'"));
        Assert.True(await DatabaseLifecycleFixture.ScalarAsync<bool>(database,
            "SELECT manifest->'destination_authentication'->>'allow_unauthenticated' = 'true' FROM integrations WHERE key = 'v29_open'"));

        Assert.False(await DatabaseLifecycleFixture.ScalarAsync<bool>(database,
            "SELECT manifest->'destination_authentication'->>'allow_unauthenticated' = 'true' FROM integrations WHERE key = 'v29_authenticated'"));
        Assert.Equal("bearer_token", await DatabaseLifecycleFixture.ScalarAsync<string>(database,
            "SELECT manifest->'destination_authentication'->'schemes'->0->>'scheme' FROM integrations WHERE key = 'v29_authenticated'"));

        Assert.Equal(0L, await DatabaseLifecycleFixture.ScalarAsync<long>(database,
            "SELECT COUNT(*) FROM integrations WHERE manifest ? 'source_verification_schemes' OR manifest ? 'destination_authentication_schemes'"));
        Assert.Equal(1L, await DatabaseLifecycleFixture.ScalarAsync<long>(database,
            "SELECT COUNT(*) FROM pg_trigger WHERE tgrelid = 'integrations'::regclass AND tgname = 'integrations_reject_functional_update' AND tgenabled = 'O'"));
    }

    private const string V27GraphSql =
        """
        INSERT INTO tenants (id, slug, name, status)
        VALUES ('27000000-0000-0000-0000-000000000001', 'v27-tenant', 'V27 Tenant', 'active');

        INSERT INTO integrations (
            id, key, contract_version, manifest_schema_version, name, direction,
            supported_auth_schemes, status, manifest)
        VALUES
            ('27000000-0000-0000-0000-000000000002', 'v27_destination', 1, 1,
             'V27 Destination', 'destination', '["bearer_token"]'::jsonb, 'active',
             '{"manifest_schema_version":1,"key":"v27_destination","contract_version":1,"direction":"destination","destination_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"source_verification_schemes":[],"destination_authentication_schemes":[{"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}],"presentation":{"name":"V27 Destination","event_types":[],"authoring_presets":[]}}'::jsonb),
            ('27000000-0000-0000-0000-000000000003', 'v27_both', 1, 1,
             'V27 Both', 'both', '[]'::jsonb, 'active',
             '{"manifest_schema_version":1,"key":"v27_both","contract_version":1,"direction":"both","source_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"destination_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},"source_verification_schemes":[],"destination_authentication_schemes":[],"presentation":{"name":"V27 Both","event_types":[],"authoring_presets":[]}}'::jsonb);

        INSERT INTO connections (id, tenant_id, integration_id, name, config, auth, status)
        VALUES
            ('27000000-0000-0000-0000-000000000004', '27000000-0000-0000-0000-000000000001',
             '27000000-0000-0000-0000-000000000002', 'v27-destination', '{}',
             '{"scheme":"bearer_token","config":{},"secret_refs":{"token":"slack_token"}}', 'active'),
            ('27000000-0000-0000-0000-000000000005', '27000000-0000-0000-0000-000000000001',
             '27000000-0000-0000-0000-000000000003', 'v27-both-open', '{}', NULL, 'active');

        INSERT INTO topics (id, tenant_id, name, status)
        VALUES
            ('27000000-0000-0000-0000-000000000006', '27000000-0000-0000-0000-000000000001', 'v27-topic', 'active'),
            ('27000000-0000-0000-0000-000000000009', '27000000-0000-0000-0000-000000000001', 'v27-disabled-topic', 'disabled');

        INSERT INTO topic_sources (tenant_id, topic_id, connection_id)
        VALUES
            ('27000000-0000-0000-0000-000000000001', '27000000-0000-0000-0000-000000000006', '27000000-0000-0000-0000-000000000005'),
            ('27000000-0000-0000-0000-000000000001', '27000000-0000-0000-0000-000000000009', '27000000-0000-0000-0000-000000000004');

        INSERT INTO subscriptions (id, tenant_id, topic_id, name, match_rules, destination_connection_id, status)
        VALUES
            ('27000000-0000-0000-0000-000000000007', '27000000-0000-0000-0000-000000000001',
             '27000000-0000-0000-0000-000000000006', 'v27-destination-subscription', '{}',
             '27000000-0000-0000-0000-000000000004', 'active'),
            ('27000000-0000-0000-0000-000000000008', '27000000-0000-0000-0000-000000000001',
             '27000000-0000-0000-0000-000000000006', 'v27-both-subscription', '{}',
             '27000000-0000-0000-0000-000000000005', 'active');
        """;
}
