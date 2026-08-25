using Integrios.Application.Delivery;
using System.Diagnostics;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Integrios.Worker.UnitTests;

public sealed class SecretValidationCliTests
{
    [Fact]
    public async Task RunAsync_ValidatesSelectedTenantAndReturnsFailureForMissingReference()
    {
        Tenant tenant = MakeTenant("tenant-a");
        Connection connection = MakeConnection(tenant.Id, "api_key");
        using ServiceProvider services = BuildServices(
            [tenant],
            [connection],
            new Dictionary<string, string>());
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SecretValidationCli.RunAsync(
            ["secrets", "validate", "--tenant", "tenant-a"], services, output, error);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain($"connection {connection.Id} / api_key: unresolvable", Case.Sensitive);
        error.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_AllValidReferencesReturnsSuccessAndSkipsInactiveConnectionsAndTenants()
    {
        Tenant tenantA = MakeTenant("tenant-a");
        Tenant tenantB = MakeTenant("tenant-b");
        Tenant disabledTenant = MakeTenant("tenant-disabled") with { Status = OperationalStatus.Disabled };
        Connection activeA = MakeConnection(tenantA.Id, "shared");
        Connection activeB = MakeConnection(tenantB.Id, "shared");
        Connection inactive = MakeConnection(tenantA.Id, "missing") with { Status = OperationalStatus.Disabled };
        Connection disabledTenantConnection = MakeConnection(disabledTenant.Id, "missing");
        using ServiceProvider services = BuildServices(
            [tenantA, tenantB, disabledTenant],
            [activeA, activeB, inactive, disabledTenantConnection],
            new Dictionary<string, string>
            {
                ["tenant-a/shared"] = "one",
                ["tenant-b/shared"] = "two"
            });
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SecretValidationCli.RunAsync(
            ["secrets", "validate", "--all"], services, output, error);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("Validated 2 secret reference(s): resolvable", Case.Sensitive);
        output.ToString().ShouldNotContain("missing", Case.Sensitive);
    }

    [Fact]
    public async Task RunAsync_DisabledTenantSelectionReturnsUsageExitCode()
    {
        Tenant tenant = MakeTenant("tenant-a") with { Status = OperationalStatus.Disabled };
        using ServiceProvider services = BuildServices([tenant], [], new Dictionary<string, string>());
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SecretValidationCli.RunAsync(
            ["secrets", "validate", "--tenant", "tenant-a"], services, output, error);

        exitCode.ShouldBe(2);
        error.ToString().ShouldContain("The selected Tenant is not active.", Case.Sensitive);
    }

    [Fact]
    public async Task RunAsync_ConnectionSelectionValidatesOnlyThatConnection()
    {
        Tenant tenant = MakeTenant("tenant-a");
        Connection selected = MakeConnection(tenant.Id, "present");
        Connection other = MakeConnection(tenant.Id, "missing");
        using ServiceProvider services = BuildServices(
            [tenant],
            [selected, other],
            new Dictionary<string, string> { ["tenant-a/present"] = "value" });
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SecretValidationCli.RunAsync(
            ["secrets", "validate", "--tenant", "tenant-a", "--connection", selected.Id.ToString()],
            services,
            output,
            error);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain(selected.Id.ToString(), Case.Sensitive);
        output.ToString().ShouldNotContain(other.Id.ToString(), Case.Sensitive);
    }

    [Fact]
    public async Task Process_InvalidStartupConfigurationReturnsUsageExitCodeWithoutStackTrace()
    {
        string workerAssembly = typeof(SecretValidationCli).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(workerAssembly);
        startInfo.ArgumentList.Add("secrets");
        startInfo.ArgumentList.Add("validate");
        startInfo.ArgumentList.Add("--all");
        startInfo.Environment["ConnectionStrings__Postgres"] = "Host=localhost;Database=integrios;Username=test;Password=test";
        startInfo.Environment["Integrios__DestinationSecrets__Provider"] = "unsupported";

        using Process process = Process.Start(startInfo)!;
        string standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(2);
        standardError.Trim().ShouldBe("Secret validation could not start with the current configuration.");
    }

    [Theory]
    [InlineData("secrets", "validate", "--all", "--tenant", "tenant-a")]
    [InlineData("secrets", "validate", "--connection", "de305d54-75b4-431b-adb2-eb6b9e546014")]
    [InlineData("secrets", "unknown", "--all")]
    public async Task RunAsync_InvalidSelectionReturnsUsage(params string[] args)
    {
        using ServiceProvider services = BuildServices([], [], new Dictionary<string, string>());
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await SecretValidationCli.RunAsync(args, services, output, error);

        exitCode.ShouldBe(2);
        error.ToString().ShouldContain("Usage: secrets validate", Case.Sensitive);
    }

    private static ServiceProvider BuildServices(
        IReadOnlyList<Tenant> tenants,
        IReadOnlyList<Connection> connections,
        IReadOnlyDictionary<string, string> secrets)
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddSingleton<ISecretValidationCatalog>(new FakeSecretValidationCatalog(tenants, connections));
        services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(secrets));
        return services.BuildServiceProvider();
    }

    private static Tenant MakeTenant(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Name = slug,
        Status = OperationalStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Connection MakeConnection(Guid tenantId, string reference) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ConnectorId = Guid.NewGuid(),
        Name = "Destination",
        Config = JsonSerializer.Deserialize<JsonElement>("{}"),
        DestinationAuthentication = new DestinationAuthentication
        {
            Scheme = "bearer",
            Config = JsonSerializer.Deserialize<JsonElement>("{}"),
            SecretRefs = JsonSerializer.Deserialize<JsonElement>($"{{\"token\":\"{reference}\"}}")
        },
        Status = OperationalStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static IDestinationAuthenticationSecretResolver CreateSecretResolver(IReadOnlyDictionary<string, string> values)
    {
        var resolver = Substitute.For<IDestinationAuthenticationSecretResolver>();
        resolver.ProviderName.Returns("test");
        resolver.ResolveAsync(Arg.Any<TenantSecretScope>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var tenant = callInfo.ArgAt<TenantSecretScope>(0);
                string secretReference = callInfo.ArgAt<string>(1);
                return values.TryGetValue($"{tenant.Slug}/{secretReference}", out string? value)
                    ? Task.FromResult(value)
                    : throw new InvalidOperationException("missing");
            });
        return resolver;
    }

    private sealed class FakeSecretValidationCatalog(
        IReadOnlyList<Tenant> tenants,
        IReadOnlyList<Connection> connections) : ISecretValidationCatalog
    {
        public Task<Tenant?> FindTenantBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenants.SingleOrDefault(item => item.Slug == slug));

        public Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Tenant>>(tenants.Where(item => item.Status == OperationalStatus.Active).ToList());

        public Task<Connection?> FindConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(connections.SingleOrDefault(item => item.TenantId == tenantId && item.Id == connectionId));

        public Task<IReadOnlyList<Connection>> ListActiveConnectionsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Connection>>(connections
                .Where(item => item.TenantId == tenantId && item.Status == OperationalStatus.Active)
                .ToList());
    }
}
