using static Integrios.FunctionalTests.Admin.AdminCliAssertions;

namespace Integrios.FunctionalTests.Admin;

public sealed class AdminCliLifecycleTests(AdminCliFixture fixture)
    : IClassFixture<AdminCliFixture>
{
    [Fact]
    public async Task FreshDatabase_RepeatedProductionSetup_IsSafeAndIdempotent()
    {
        CliDatabase database = await fixture.CreateDatabaseAsync();

        AdminProcessResult missingSecret = await AdminCliFixture.RunProductionSetupAsync(database, secret: null);

        missingSecret.ExitCode.ShouldNotBe(0);
        missingSecret.StandardError.ShouldContain("requires a non-empty", Case.Insensitive);
        missingSecret.Output.ShouldNotContain("shown once", Case.Insensitive);
        (await CountAsync(database, "information_schema.tables", "table_schema = 'public'")).ShouldBe(0L);

        const string suppliedSecret = "cli-production-secret";
        AdminProcessResult firstSetup = await AdminCliFixture.RunProductionSetupAsync(database, suppliedSecret);

        firstSetup.ExitCode.ShouldBe(0, firstSetup.Output);
        firstSetup.Output.ShouldNotContain(suppliedSecret, Case.Sensitive);
        (await ColumnShapeAsync(database, "event_deliveries", "connector_key")).ShouldBe("text|NO");
        (await ColumnShapeAsync(database, "event_deliveries", "http_execution_snapshot")).ShouldBe("jsonb|YES");
        (await ColumnShapeAsync(database, "subscriptions", "http_delivery")).ShouldBe("jsonb|YES");
        (await CountColumnsAsync(database, "event_deliveries", "destination_url", "destination_auth")).ShouldBe(0L);
        (await ColumnShapeAsync(database, "event_deliveries", "active_attempt_id")).ShouldBe("uuid|YES");
        (await ColumnShapeAsync(database, "event_deliveries", "lease_expires_at")).ShouldBe("timestamp with time zone|YES");
        (await ColumnShapeAsync(database, "delivery_attempts", "event_delivery_id")).ShouldBe("uuid|NO");
        (await ColumnShapeAsync(database, "delivery_attempts", "failure_phase")).ShouldBe("text|YES");
        (await CountColumnsAsync(database, "subscriptions", "delivery_policy", "dlq_enabled")).ShouldBe(0L);
        (await CountColumnsAsync(database, "operator_keys", "tenant_id")).ShouldBe(0L);
        (await CountColumnsAsync(database, "tenant_api_keys", "scopes")).ShouldBe(0L);
        (await CountAsync(database, "connectors", "key = 'pull'")).ShouldBe(1L);
        (await CountAsync(database, "connectors", "key <> 'pull'")).ShouldBe(0L);
        (await CountAsync(database, "pg_roles", "rolname IN ('admin_runtime', 'data_runtime')")).ShouldBe(2L);
        (await CountAsync(database, "operator_keys", "revoked_at IS NULL")).ShouldBe(1L);

        AdminProcessResult secondSetup = await AdminCliFixture.RunProductionSetupAsync(database, "unused-second-secret");

        secondSetup.ExitCode.ShouldBe(0, secondSetup.Output);
        secondSetup.StandardOutput.ShouldContain("no-op", Case.Insensitive);
        secondSetup.Output.ShouldNotContain("unused-second-secret", Case.Sensitive);
        (await CountAsync(database, "connectors", "key <> 'pull'")).ShouldBe(0L);
        (await CountAsync(database, "operator_keys")).ShouldBe(1L);
        (await AdminCliFixture.ScalarAsync<string>(
            database,
            "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(Hash(suppliedSecret));
    }

    [Fact]
    public async Task OperatorKeyRotation_RequiresOutOfBandSecretAndDoesNotDiscloseIt()
    {
        CliDatabase database = await fixture.CreateDatabaseAsync();
        (await AdminCliFixture.RunDatabaseMigrationAsync(database)).ExitCode.ShouldBe(0);

        AdminProcessResult beforeBootstrap =
            await AdminCliFixture.RunOperatorKeyRotationAsync(database, "premature-rotation-secret");
        beforeBootstrap.ExitCode.ShouldNotBe(0);
        beforeBootstrap.StandardError.ShouldContain("Run bootstrap before rotation", Case.Sensitive);
        (await CountAsync(database, "operator_keys")).ShouldBe(0L);

        const string oldSecret = "rotation-old-secret";
        (await AdminCliFixture.RunProductionSetupAsync(database, oldSecret)).ExitCode.ShouldBe(0);

        AdminProcessResult missingSecret =
            await AdminCliFixture.RunOperatorKeyRotationAsync(database, secret: null);
        missingSecret.ExitCode.ShouldNotBe(0);
        (await AdminCliFixture.ScalarAsync<string>(
            database, "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(Hash(oldSecret));

        const string replacementSecret = "rotation-replacement-secret";
        AdminProcessResult rotated =
            await AdminCliFixture.RunOperatorKeyRotationAsync(database, replacementSecret);

        rotated.ExitCode.ShouldBe(0);
        rotated.Output.ShouldNotContain(replacementSecret, Case.Sensitive);
        string publicKey = await AdminCliFixture.ScalarAsync<string>(
            database, "SELECT public_key FROM operator_keys WHERE revoked_at IS NULL");
        rotated.StandardOutput.ShouldContain(publicKey, Case.Sensitive);
        (await AdminCliFixture.ScalarAsync<string>(
            database, "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(Hash(replacementSecret));
        (await CountAsync(database, "operator_keys", "revoked_at IS NULL")).ShouldBe(1L);
        (await CountAsync(database, "operator_keys", "secret_hash = '" + Hash(oldSecret) + "' AND revoked_at IS NOT NULL")).ShouldBe(1L);
    }
}
