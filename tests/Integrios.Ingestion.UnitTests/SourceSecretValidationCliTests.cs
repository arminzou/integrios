using System.Diagnostics;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Ingestion;
using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Ingestion.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Ingestion.UnitTests;

public sealed class SourceSecretValidationCliTests
{
    [Fact]
    public async Task RunAsync_ReportsAWebhookReferenceTheMountCannotResolve()
    {
        Tenant tenant = MakeTenant("tenant-a");
        Source source = WebhookSource(tenant.Id, "github-secret");
        using ServiceProvider services = BuildServices([tenant], [source], new Dictionary<string, string>());
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SourceSecretValidationCli.RunAsync(
            ["secrets", "validate", "--tenant", "tenant-a"], services, output, error);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain($"source {source.Id} / github-secret: unresolvable", Case.Sensitive);
        error.ToString().ShouldBeEmpty();
    }

    // The two shapes a Source carries: verification holds a {field: reference} object, a broker holds
    // one bare reference inside its transport configuration. Covering only the first would leave
    // every broker credential unchecked.
    [Fact]
    public async Task RunAsync_CoversBrokerAndWebhookShapesAndDisabledSourcesButSkipsInactiveTenants()
    {
        Tenant tenant = MakeTenant("tenant-a");
        Tenant disabled = MakeTenant("tenant-disabled") with { Status = OperationalStatus.Inactive };
        Source webhook = WebhookSource(tenant.Id, "hook-secret");
        Source broker = BrokerSource(tenant.Id, "bus-connection");
        // Inactive is reversible, so its secrets have to resolve before it is activated, not after.
        Source paused = WebhookSource(tenant.Id, "paused-secret") with { Status = OperationalStatus.Inactive };
        Source otherTenant = WebhookSource(disabled.Id, "ignored-secret");
        using ServiceProvider services = BuildServices(
            [tenant, disabled],
            [webhook, broker, paused, otherTenant],
            new Dictionary<string, string>
            {
                ["tenant-a/hook-secret"] = "hook-value",
                ["tenant-a/bus-connection"] = "bus-value",
                ["tenant-a/paused-secret"] = "paused-value",
            });
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SourceSecretValidationCli.RunAsync(
            ["secrets", "validate", "--all"], services, output, error);

        exitCode.ShouldBe(0);
        string report = output.ToString();
        report.ShouldContain("hook-secret: resolvable", Case.Sensitive);
        report.ShouldContain("bus-connection: resolvable", Case.Sensitive);
        report.ShouldContain("paused-secret: resolvable", Case.Sensitive);
        report.ShouldNotContain("ignored-secret", Case.Sensitive);
    }

    [Fact]
    public async Task RunAsync_NarrowsToOneSource()
    {
        Tenant tenant = MakeTenant("tenant-a");
        Source selected = WebhookSource(tenant.Id, "selected-secret");
        Source other = WebhookSource(tenant.Id, "other-secret");
        using ServiceProvider services = BuildServices(
            [tenant],
            [selected, other],
            new Dictionary<string, string>
            {
                ["tenant-a/selected-secret"] = "value",
                ["tenant-a/other-secret"] = "value",
            });
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SourceSecretValidationCli.RunAsync(
            ["secrets", "validate", "--tenant", "tenant-a", "--source", selected.Id.ToString()],
            services,
            output,
            error);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain(selected.Id.ToString(), Case.Sensitive);
        output.ToString().ShouldNotContain(other.Id.ToString(), Case.Sensitive);
    }

    [Theory]
    [InlineData("secrets", "validate", "--all", "--tenant", "tenant-a")]
    [InlineData("secrets", "validate", "--source", "de305d54-75b4-431b-adb2-eb6b9e546014")]
    [InlineData("secrets", "unknown", "--all")]
    public async Task RunAsync_InvalidSelectionReturnsUsage(params string[] args)
    {
        using ServiceProvider services = BuildServices([], [], new Dictionary<string, string>());
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SourceSecretValidationCli.RunAsync(args, services, output, error);

        exitCode.ShouldBe(2);
        error.ToString().ShouldContain("Usage: secrets validate", Case.Sensitive);
    }

    [Fact]
    public async Task Process_UnreachableKeyVaultFailsStartupWithUsageExitCodeWithoutStackTrace()
    {
        string ingestionAssembly = typeof(SourceSecretValidationCli).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(ingestionAssembly);
        startInfo.ArgumentList.Add("secrets");
        startInfo.ArgumentList.Add("validate");
        startInfo.ArgumentList.Add("--all");
        startInfo.Environment["ConnectionStrings__Postgres"] = "Host=localhost;Database=integrios;Username=test;Password=test";
        startInfo.Environment["Integrios__KeyVault__Uri"] = "https://integrios-unreachable.invalid/";

        using Process process = Process.Start(startInfo)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // The vault loads before the host is built, so its failure precedes any database work;
        // output from that work would mean the vault source was never added.
        process.ExitCode.ShouldBe(2);
        (await standardOutput).ShouldBeEmpty();
        (await standardError).Trim().ShouldBe("Secret validation could not start with the current configuration.");
    }

    private static ServiceProvider BuildServices(
        IReadOnlyList<Tenant> tenants,
        IReadOnlyList<Source> sources,
        IReadOnlyDictionary<string, string> secrets)
    {
        var services = new ServiceCollection();
        services.AddIngestionApplicationServices();
        services.AddSingleton<ISourceSecretValidationReader>(new FakeSourceSecretValidationReader(tenants, sources));
        services.AddSingleton<ISourceVerificationSecretResolver>(new FakeSourceSecretResolver(secrets));
        return services.BuildServiceProvider();
    }

    private static Tenant MakeTenant(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Name = slug,
        Status = OperationalStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Source WebhookSource(Guid tenantId, string reference) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ConnectorId = Guid.NewGuid(),
        TopicId = Guid.NewGuid(),
        Name = "webhook-intake",
        Type = SourceType.Webhook,
        EventTypes = ["order.created"],
        Configuration = Json("{}"),
        Verification = new SourceVerification
        {
            Scheme = "hmac_sha256",
            Config = Json("{}"),
            SecretRefs = JsonSerializer.SerializeToElement(new { secret = reference }),
        },
        Revision = Guid.NewGuid().ToString("N"),
        Status = OperationalStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Source BrokerSource(Guid tenantId, string reference) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ConnectorId = Guid.NewGuid(),
        TopicId = Guid.NewGuid(),
        Name = "broker-intake",
        Type = SourceType.Broker,
        EventTypes = ["order.created"],
        Configuration = JsonSerializer.SerializeToElement(new
        {
            transport = "azure_service_bus",
            authentication = new { scheme = "connection_string", secret_ref = reference },
            transport_config = new { @namespace = "acme.servicebus.windows.net", queue_name = "events" },
        }),
        Revision = Guid.NewGuid().ToString("N"),
        Status = OperationalStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private sealed class FakeSourceSecretValidationReader(
        IReadOnlyList<Tenant> tenants,
        IReadOnlyList<Source> sources) : ISourceSecretValidationReader
    {
        public Task<Tenant?> FindTenantBySlugAsync(string slug, CancellationToken cancellationToken) =>
            Task.FromResult(tenants.SingleOrDefault(tenant => tenant.Slug == slug));

        public Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Tenant>>(
                [.. tenants.Where(tenant => tenant.Status == OperationalStatus.Active)]);

        public Task<Source?> FindSourceAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(sources.SingleOrDefault(
                source => source.TenantId == tenantId && source.Id == sourceId));

        public Task<IReadOnlyList<Source>> ListSourcesAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Source>>(
                [.. sources.Where(source => source.TenantId == tenantId)]);
    }

    private sealed class FakeSourceSecretResolver(IReadOnlyDictionary<string, string> secrets)
        : ISourceVerificationSecretResolver
    {
        public string ProviderName => "fake";

        public Task<string> ResolveAsync(
            TenantSecretScope tenant,
            string secretReference,
            CancellationToken cancellationToken) =>
            secrets.TryGetValue($"{tenant.Slug}/{secretReference}", out string? value)
                ? Task.FromResult(value)
                : throw new InvalidOperationException($"No secret for {secretReference}.");
    }
}
