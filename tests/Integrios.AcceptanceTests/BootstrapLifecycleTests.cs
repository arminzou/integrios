using static Integrios.AcceptanceTests.DatabaseLifecycleAssertions;

namespace Integrios.AcceptanceTests;

[Trait("Category", "Qualification")]
public sealed class BootstrapLifecycleTests(DatabaseLifecycleFixture fixture)
    : IClassFixture<DatabaseLifecycleFixture>
{
    [Fact]
    public async Task FreshDatabase_RepeatedEfMigrationAndProductionBootstrap_AreSafeAndIdempotent()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();

        BootstrapProcessResult firstMigrate = await DatabaseLifecycleFixture.RunDatabaseMigrationAsync(database);
        BootstrapProcessResult secondMigrate = await DatabaseLifecycleFixture.RunDatabaseMigrationAsync(database);

        Assert.Equal(0, firstMigrate.ExitCode);
        Assert.Equal(0, secondMigrate.ExitCode);
        Assert.Equal(3L, await DatabaseLifecycleFixture.ScalarAsync<long>(
            database, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
        Assert.Equal("text|NO", await ColumnShapeAsync(database, "event_deliveries", "connector_key"));
        Assert.Equal("jsonb|NO", await ColumnShapeAsync(database, "event_deliveries", "http_execution_snapshot"));
        Assert.Equal("jsonb|NO", await ColumnShapeAsync(database, "subscriptions", "http_delivery"));
        Assert.Equal(0L, await CountColumnsAsync(database, "event_deliveries", "destination_url", "destination_auth"));
        Assert.Equal("uuid|YES", await ColumnShapeAsync(database, "event_deliveries", "active_attempt_id"));
        Assert.Equal("timestamp with time zone|YES", await ColumnShapeAsync(database, "event_deliveries", "lease_expires_at"));
        Assert.Equal("uuid|NO", await ColumnShapeAsync(database, "delivery_attempts", "event_delivery_id"));
        Assert.Equal("text|YES", await ColumnShapeAsync(database, "delivery_attempts", "failure_phase"));
        Assert.Equal(0L, await CountColumnsAsync(database, "subscriptions", "delivery_policy", "dlq_enabled"));
        Assert.Equal(0L, await CountColumnsAsync(database, "operator_keys", "tenant_id"));
        Assert.Equal(0L, await CountColumnsAsync(database, "tenant_api_keys", "scopes"));
        Assert.Equal(0L, await CountAsync(database, "connectors"));
        Assert.Equal(0L, await CountAsync(database, "operator_keys"));

        BootstrapProcessResult missingSecret =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, secret: null);

        Assert.NotEqual(0, missingSecret.ExitCode);
        Assert.Contains("requires a non-empty", missingSecret.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shown once", missingSecret.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await CountAsync(database, "connectors"));
        Assert.Equal(0L, await CountAsync(database, "operator_keys"));

        const string suppliedSecret = "qualification-production-secret";
        BootstrapProcessResult firstBootstrap =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, suppliedSecret);

        Assert.Equal(0, firstBootstrap.ExitCode);
        Assert.DoesNotContain(suppliedSecret, firstBootstrap.Output, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync(database, "connectors", "key = 'http'"));
        Assert.Equal(1L, await CountAsync(database, "operator_keys", "revoked_at IS NULL"));

        await ExecuteAsync(database, "UPDATE connectors SET name = 'Drifted', status = 'disabled' WHERE key = 'http'");

        BootstrapProcessResult secondBootstrap =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, "unused-second-secret");

        Assert.Equal(0, secondBootstrap.ExitCode);
        Assert.Contains("no-op", secondBootstrap.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unused-second-secret", secondBootstrap.Output, StringComparison.Ordinal);
        Assert.Equal("HTTP|both|active", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT name || '|' || direction || '|' || status FROM connectors WHERE key = 'http'"));
        Assert.Equal(2, await DatabaseLifecycleFixture.ScalarAsync<int>(
            database,
            "SELECT jsonb_array_length(supported_auth_schemes) FROM connectors WHERE key = 'http'"));
        Assert.True(await DatabaseLifecycleFixture.ScalarAsync<bool>(
            database,
            "SELECT supported_auth_schemes @> '[\"api_key_header\", \"bearer_token\"]'::jsonb FROM connectors WHERE key = 'http'"));
        Assert.Equal(Hash(suppliedSecret), await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL"));
    }

    [Fact]
    public async Task OperatorKeyRotation_RequiresOutOfBandSecretAndDoesNotDiscloseIt()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        Assert.Equal(0, (await DatabaseLifecycleFixture.RunDatabaseMigrationAsync(database)).ExitCode);

        BootstrapProcessResult beforeBootstrap =
            await DatabaseLifecycleFixture.RunOperatorKeyRotationAsync(database, "premature-rotation-secret");
        Assert.NotEqual(0, beforeBootstrap.ExitCode);
        Assert.Contains("Run bootstrap before rotation", beforeBootstrap.StandardError, StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync(database, "operator_keys"));

        const string oldSecret = "rotation-old-secret";
        Assert.Equal(0, (await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, oldSecret)).ExitCode);

        BootstrapProcessResult missingSecret =
            await DatabaseLifecycleFixture.RunOperatorKeyRotationAsync(database, secret: null);
        Assert.NotEqual(0, missingSecret.ExitCode);
        Assert.Equal(Hash(oldSecret), await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL"));

        const string replacementSecret = "rotation-replacement-secret";
        BootstrapProcessResult rotated =
            await DatabaseLifecycleFixture.RunOperatorKeyRotationAsync(database, replacementSecret);

        Assert.Equal(0, rotated.ExitCode);
        Assert.DoesNotContain(replacementSecret, rotated.Output, StringComparison.Ordinal);
        string publicKey = await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT public_key FROM operator_keys WHERE revoked_at IS NULL");
        Assert.Contains(publicKey, rotated.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(Hash(replacementSecret), await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL"));
        Assert.Equal(1L, await CountAsync(database, "operator_keys", "revoked_at IS NULL"));
        Assert.Equal(1L, await CountAsync(database, "operator_keys", "secret_hash = '" + Hash(oldSecret) + "' AND revoked_at IS NOT NULL"));
    }
}
