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
}
