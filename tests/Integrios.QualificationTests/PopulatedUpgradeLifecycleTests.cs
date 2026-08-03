using static Integrios.QualificationTests.DatabaseLifecycleAssertions;

namespace Integrios.QualificationTests;

[Trait("Category", "Qualification")]
[Trait("Tier", "database")]
public sealed class PopulatedUpgradeLifecycleTests(DatabaseLifecycleFixture fixture)
    : IClassFixture<DatabaseLifecycleFixture>
{
    [Fact]
    public async Task PopulatedV17Database_UpgradesWithoutReinterpretingReferencedIntegration()
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
            "SELECT name || '|' || direction || '|' || status FROM integrations WHERE key = 'http'"));
        Assert.Equal("destination", await DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            "SELECT manifest->>'direction' FROM integrations WHERE key = 'http' AND contract_version = 1"));

        BootstrapProcessResult bootstrap = await DatabaseLifecycleFixture.RunProductionBootstrapAsync(
            database,
            "v17-upgrade-secret");

        Assert.NotEqual(0, bootstrap.ExitCode);
        Assert.DoesNotContain("v17-upgrade-secret", bootstrap.Output, StringComparison.Ordinal);
        Assert.Contains("different functional contract", bootstrap.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "Drifted Webhook|destination|disabled|[\"api_key_header\", \"bearer_token\"]",
            await DatabaseLifecycleFixture.ScalarAsync<string>(
                database,
                "SELECT name || '|' || direction || '|' || status || '|' || supported_auth_schemes::text FROM integrations WHERE key = 'http'"));
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
            "SELECT secret_hash FROM admin_keys WHERE public_key = 'global_admin_key'"));
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
            "SELECT secret_hash FROM admin_keys WHERE public_key = 'global_admin_key'"));
        Assert.Equal(
            "HTTP|both|active|[\"api_key_header\", \"bearer_token\"]",
            await DatabaseLifecycleFixture.ScalarAsync<string>(
                database,
                "SELECT name || '|' || direction || '|' || status || '|' || supported_auth_schemes::text FROM integrations WHERE key = 'http'"));
    }
}
