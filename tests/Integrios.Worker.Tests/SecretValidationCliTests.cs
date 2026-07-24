using System.Text.Json;
using System.Diagnostics;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Domain.Tenants;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Worker.Tests;

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

        Assert.Equal(1, exitCode);
        Assert.Contains($"connection {connection.Id} / api_key: unresolvable", output.ToString());
        Assert.Empty(error.ToString());
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

        Assert.Equal(0, exitCode);
        Assert.Contains("Validated 2 secret reference(s): resolvable", output.ToString());
        Assert.DoesNotContain("missing", output.ToString());
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

        Assert.Equal(2, exitCode);
        Assert.Contains("The selected Tenant is not active.", error.ToString());
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

        Assert.Equal(0, exitCode);
        Assert.Contains(selected.Id.ToString(), output.ToString());
        Assert.DoesNotContain(other.Id.ToString(), output.ToString());
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
        startInfo.Environment["Integrios__Secrets__Provider"] = "unsupported";

        using Process process = Process.Start(startInfo)!;
        string standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Equal("Secret validation could not start with the current configuration.", standardError.Trim());
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

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage: secrets validate", error.ToString());
    }

    private static ServiceProvider BuildServices(
        IReadOnlyList<Tenant> tenants,
        IReadOnlyList<Connection> connections,
        IReadOnlyDictionary<string, string> secrets)
    {
        var services = new ServiceCollection();
        services.AddIntegriosApplication();
        services.AddSingleton<ITenantRepository>(new FakeTenantRepository(tenants));
        services.AddSingleton<IConnectionRepository>(new FakeConnectionRepository(connections));
        services.AddSingleton<ISecretResolver>(new FakeSecretResolver(secrets));
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
        IntegrationId = Guid.NewGuid(),
        Name = "Destination",
        Config = JsonSerializer.Deserialize<JsonElement>("{}"),
        Auth = new ConnectionAuth
        {
            Scheme = "bearer",
            Config = JsonSerializer.Deserialize<JsonElement>("{}"),
            SecretRefs = JsonSerializer.Deserialize<JsonElement>($"{{\"token\":\"{reference}\"}}")
        },
        Status = OperationalStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class FakeSecretResolver(IReadOnlyDictionary<string, string> values) : ISecretResolver
    {
        public string ProviderName => "test";

        public Task<string> ResolveAsync(TenantSecretScope tenant, string secretReference, CancellationToken cancellationToken = default) =>
            values.TryGetValue($"{tenant.Slug}/{secretReference}", out string? value)
                ? Task.FromResult(value)
                : throw new InvalidOperationException("missing");
    }

    private sealed class FakeTenantRepository(IReadOnlyList<Tenant> tenants) : ITenantRepository
    {
        public Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenants.SingleOrDefault(item => item.Slug == slug));
        public Task<(IReadOnlyList<Tenant> Items, string? NextCursor)> ListAsync(string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult((tenants, (string?)null));
        public Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Tenant?> UpdateAsync(Guid id, string name, string? description, string? environment, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeConnectionRepository(IReadOnlyList<Connection> connections) : IConnectionRepository
    {
        public Task<Connection?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(connections.SingleOrDefault(item => item.TenantId == tenantId && item.Id == id));
        public Task<(IReadOnlyList<Connection> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(((IReadOnlyList<Connection>)connections.Where(item => item.TenantId == tenantId).ToList(), (string?)null));
        public Task<Connection> CreateAsync(Connection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Connection?> UpdateAsync(Guid tenantId, Guid id, string name, JsonElement config, ConnectionAuth? auth, string? environment, string? description, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
