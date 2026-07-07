using Integrios.Application.Abstractions.Auth;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Http.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Worker.Tests;

public sealed class EnvironmentSecretResolverTests : IDisposable
{
    private readonly List<string> variablesToClear = [];

    [Fact]
    public async Task ResolveAsync_ReturnsPrefixedEnvironmentVariable()
    {
        string reference = UniqueReference();
        string key = EnvironmentSecretResolver.Prefix + reference.ToUpperInvariant();
        SetEnvironmentVariable(key, "resolved-value");

        var resolver = new EnvironmentSecretResolver();

        string value = await resolver.ResolveAsync(Guid.NewGuid(), reference);

        Assert.Equal("resolved-value", value);
    }

    [Fact]
    public async Task ResolveAsync_IgnoresRawEnvironmentVariableNamesOutsidePrefix()
    {
        string reference = UniqueReference();
        SetEnvironmentVariable(reference, "raw-value");

        var resolver = new EnvironmentSecretResolver();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(Guid.NewGuid(), reference));

        Assert.Contains(EnvironmentSecretResolver.Prefix, error.Message);
    }

    [Fact]
    public void AddIntegriosInfrastructure_UsesEnvironmentSecretResolverByDefault()
    {
        var services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios;Username=test;Password=test"
            })
            .Build();

        services.AddIntegriosInfrastructure(config);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<EnvironmentSecretResolver>(provider.GetRequiredService<ISecretResolver>());
    }

    public void Dispose()
    {
        foreach (string variable in variablesToClear)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private void SetEnvironmentVariable(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        variablesToClear.Add(key);
    }

    private static string UniqueReference() => $"resolver_{Guid.NewGuid():N}";
}
