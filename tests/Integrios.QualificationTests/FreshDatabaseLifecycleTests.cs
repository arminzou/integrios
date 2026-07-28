using static Integrios.QualificationTests.DatabaseLifecycleAssertions;

namespace Integrios.QualificationTests;

[Trait("Category", "Qualification")]
public sealed class FreshDatabaseLifecycleTests(DatabaseLifecycleFixture fixture)
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
        Assert.Equal(24L, await DatabaseLifecycleFixture.ScalarAsync<long>(
            database, "SELECT COUNT(*) FROM flyway_schema_history WHERE success"));
        Assert.Equal("text|YES", await ColumnShapeAsync(database, "subscription_deliveries", "destination_url"));
        Assert.Equal("text|NO", await ColumnShapeAsync(database, "subscription_deliveries", "integration_key"));
        Assert.Equal("jsonb|YES", await ColumnShapeAsync(database, "subscription_deliveries", "destination_auth"));
        Assert.Equal("uuid|YES", await ColumnShapeAsync(database, "subscription_deliveries", "active_attempt_id"));
        Assert.Equal("timestamp with time zone|YES", await ColumnShapeAsync(database, "subscription_deliveries", "lease_expires_at"));
        Assert.Equal("uuid|NO", await ColumnShapeAsync(database, "delivery_attempts", "subscription_delivery_id"));
        Assert.Equal("text|YES", await ColumnShapeAsync(database, "delivery_attempts", "failure_phase"));
        Assert.Equal(0L, await CountColumnsAsync(database, "subscriptions", "delivery_policy", "dlq_enabled"));
        Assert.Equal(0L, await CountColumnsAsync(database, "admin_keys", "tenant_id"));
        Assert.Equal(0L, await CountColumnsAsync(database, "api_keys", "scopes"));
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
        Assert.Equal(1L, await CountAsync(database, "admin_keys", "revoked_at IS NULL"));

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
            "SELECT secret_hash FROM admin_keys WHERE revoked_at IS NULL"));
    }

    [Fact]
    public async Task AdminKeyRotation_RequiresOutOfBandSecretAndDoesNotDiscloseIt()
    {
        QualificationDatabase database = await fixture.CreateDatabaseAsync();
        await fixture.RunFlywayAsync(database, "migrate");

        BootstrapProcessResult beforeBootstrap =
            await DatabaseLifecycleFixture.RunAdminKeyRotationAsync(database, "premature-rotation-secret");
        Assert.NotEqual(0, beforeBootstrap.ExitCode);
        Assert.Contains("Run bootstrap before rotation", beforeBootstrap.StandardError, StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync(database, "admin_keys"));

        const string oldSecret = "rotation-old-secret";
        Assert.Equal(0, (await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, oldSecret)).ExitCode);

        BootstrapProcessResult missingSecret =
            await DatabaseLifecycleFixture.RunAdminKeyRotationAsync(database, secret: null);
        Assert.NotEqual(0, missingSecret.ExitCode);
        Assert.Equal(Hash(oldSecret), await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT secret_hash FROM admin_keys WHERE revoked_at IS NULL"));

        const string replacementSecret = "rotation-replacement-secret";
        BootstrapProcessResult rotated =
            await DatabaseLifecycleFixture.RunAdminKeyRotationAsync(database, replacementSecret);

        Assert.Equal(0, rotated.ExitCode);
        Assert.DoesNotContain(replacementSecret, rotated.Output, StringComparison.Ordinal);
        string publicKey = await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT public_key FROM admin_keys WHERE revoked_at IS NULL");
        Assert.Contains(publicKey, rotated.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(Hash(replacementSecret), await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT secret_hash FROM admin_keys WHERE revoked_at IS NULL"));
        Assert.Equal(1L, await CountAsync(database, "admin_keys", "revoked_at IS NULL"));
        Assert.Equal(1L, await CountAsync(database, "admin_keys", "secret_hash = '" + Hash(oldSecret) + "' AND revoked_at IS NOT NULL"));
    }
}
