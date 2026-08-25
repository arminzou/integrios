using static Integrios.AcceptanceTests.DatabaseLifecycleAssertions;

namespace Integrios.AcceptanceTests;

public sealed class BootstrapLifecycleTests(DatabaseLifecycleFixture fixture)
    : IClassFixture<DatabaseLifecycleFixture>
{
    [Fact]
    public async Task FreshDatabase_RepeatedEfMigrationAndProductionBootstrap_AreSafeAndIdempotent()
    {
        AcceptanceDatabase database = await fixture.CreateDatabaseAsync();

        BootstrapProcessResult firstMigrate = await DatabaseLifecycleFixture.RunDatabaseMigrationAsync(database);
        BootstrapProcessResult secondMigrate = await DatabaseLifecycleFixture.RunDatabaseMigrationAsync(database);

        firstMigrate.ExitCode.ShouldBe(0);
        secondMigrate.ExitCode.ShouldBe(0);
        (await ColumnShapeAsync(database, "event_deliveries", "connector_key")).ShouldBe("text|NO");
        (await ColumnShapeAsync(database, "event_deliveries", "http_execution_snapshot")).ShouldBe("jsonb|NO");
        (await ColumnShapeAsync(database, "subscriptions", "http_delivery")).ShouldBe("jsonb|NO");
        (await CountColumnsAsync(database, "event_deliveries", "destination_url", "destination_auth")).ShouldBe(0L);
        (await ColumnShapeAsync(database, "event_deliveries", "active_attempt_id")).ShouldBe("uuid|YES");
        (await ColumnShapeAsync(database, "event_deliveries", "lease_expires_at")).ShouldBe("timestamp with time zone|YES");
        (await ColumnShapeAsync(database, "delivery_attempts", "event_delivery_id")).ShouldBe("uuid|NO");
        (await ColumnShapeAsync(database, "delivery_attempts", "failure_phase")).ShouldBe("text|YES");
        (await CountColumnsAsync(database, "subscriptions", "delivery_policy", "dlq_enabled")).ShouldBe(0L);
        (await CountColumnsAsync(database, "operator_keys", "tenant_id")).ShouldBe(0L);
        (await CountColumnsAsync(database, "tenant_api_keys", "scopes")).ShouldBe(0L);
        (await CountAsync(database, "connectors")).ShouldBe(0L);
        (await CountAsync(database, "operator_keys")).ShouldBe(0L);

        BootstrapProcessResult missingSecret =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, secret: null);

        missingSecret.ExitCode.ShouldNotBe(0);
        missingSecret.StandardError.ShouldContain("requires a non-empty", Case.Insensitive);
        missingSecret.Output.ShouldNotContain("shown once", Case.Insensitive);
        (await CountAsync(database, "connectors")).ShouldBe(0L);
        (await CountAsync(database, "operator_keys")).ShouldBe(0L);

        const string suppliedSecret = "acceptance-production-secret";
        BootstrapProcessResult firstBootstrap =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, suppliedSecret);

        firstBootstrap.ExitCode.ShouldBe(0);
        firstBootstrap.Output.ShouldNotContain(suppliedSecret, Case.Sensitive);
        (await CountAsync(database, "connectors", "key = 'http'")).ShouldBe(1L);
        (await CountAsync(database, "operator_keys", "revoked_at IS NULL")).ShouldBe(1L);

        await ExecuteAsync(database, "UPDATE connectors SET name = 'Drifted', status = 'disabled' WHERE key = 'http'");

        BootstrapProcessResult secondBootstrap =
            await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, "unused-second-secret");

        secondBootstrap.ExitCode.ShouldBe(0);
        secondBootstrap.StandardOutput.ShouldContain("no-op", Case.Insensitive);
        secondBootstrap.Output.ShouldNotContain("unused-second-secret", Case.Sensitive);
        (await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT name || '|' || direction || '|' || status FROM connectors WHERE key = 'http'")).ShouldBe("HTTP|both|active");
        (await DatabaseLifecycleFixture.ScalarAsync<int>(
            database,
            "SELECT jsonb_array_length(manifest->'destination_authentication'->'schemes') FROM connectors WHERE key = 'http'")).ShouldBe(2);
        (await DatabaseLifecycleFixture.ScalarAsync<bool>(
            database,
            "SELECT (SELECT jsonb_agg(s->>'scheme') FROM jsonb_array_elements(manifest->'destination_authentication'->'schemes') s) "
            + "@> '[\"api_key_header\", \"bearer_token\"]'::jsonb FROM connectors WHERE key = 'http'")).ShouldBeTrue();
        (await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(Hash(suppliedSecret));
    }

    [Fact]
    public async Task OperatorKeyRotation_RequiresOutOfBandSecretAndDoesNotDiscloseIt()
    {
        AcceptanceDatabase database = await fixture.CreateDatabaseAsync();
        (await DatabaseLifecycleFixture.RunDatabaseMigrationAsync(database)).ExitCode.ShouldBe(0);

        BootstrapProcessResult beforeBootstrap =
            await DatabaseLifecycleFixture.RunOperatorKeyRotationAsync(database, "premature-rotation-secret");
        beforeBootstrap.ExitCode.ShouldNotBe(0);
        beforeBootstrap.StandardError.ShouldContain("Run bootstrap before rotation", Case.Sensitive);
        (await CountAsync(database, "operator_keys")).ShouldBe(0L);

        const string oldSecret = "rotation-old-secret";
        (await DatabaseLifecycleFixture.RunProductionBootstrapAsync(database, oldSecret)).ExitCode.ShouldBe(0);

        BootstrapProcessResult missingSecret =
            await DatabaseLifecycleFixture.RunOperatorKeyRotationAsync(database, secret: null);
        missingSecret.ExitCode.ShouldNotBe(0);
        (await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(Hash(oldSecret));

        const string replacementSecret = "rotation-replacement-secret";
        BootstrapProcessResult rotated =
            await DatabaseLifecycleFixture.RunOperatorKeyRotationAsync(database, replacementSecret);

        rotated.ExitCode.ShouldBe(0);
        rotated.Output.ShouldNotContain(replacementSecret, Case.Sensitive);
        string publicKey = await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT public_key FROM operator_keys WHERE revoked_at IS NULL");
        rotated.StandardOutput.ShouldContain(publicKey, Case.Sensitive);
        (await DatabaseLifecycleFixture.ScalarAsync<string>(
            database, "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(Hash(replacementSecret));
        (await CountAsync(database, "operator_keys", "revoked_at IS NULL")).ShouldBe(1L);
        (await CountAsync(database, "operator_keys", "secret_hash = '" + Hash(oldSecret) + "' AND revoked_at IS NOT NULL")).ShouldBe(1L);
    }
}
