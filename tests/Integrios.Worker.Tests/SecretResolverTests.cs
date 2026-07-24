using System.Text;
using Integrios.Application.Abstractions.Auth;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Http.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Worker.Tests;

public sealed class SecretResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"integrios-secrets-{Guid.NewGuid():N}");

    public SecretResolverTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task FileResolver_IsolatesSameReferenceByTenantAndPreservesExactValue()
    {
        WriteSecret("tenant-a", "api_key", "first\n");
        WriteSecret("tenant-b", "api_key", "second");
        var resolver = new MountedFileSecretResolver(root);

        string tenantA = await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key");
        string tenantB = await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-b"), "api_key");

        Assert.Equal("first\n", tenantA);
        Assert.Equal("second", tenantB);
    }

    [Fact]
    public async Task FileResolver_ReadsEveryLookupSoRotationIsVisible()
    {
        WriteSecret("tenant-a", "api_key", "before");
        var resolver = new MountedFileSecretResolver(root);
        TenantSecretScope tenant = new(Guid.NewGuid(), "tenant-a");

        Assert.Equal("before", await resolver.ResolveAsync(tenant, "api_key"));
        WriteSecret("tenant-a", "api_key", "after");
        Assert.Equal("after", await resolver.ResolveAsync(tenant, "api_key"));
    }

    [Fact]
    public async Task FileResolver_FollowsSymlinks()
    {
        string tenantDirectory = TenantDirectory("tenant-a");
        string target = Path.Combine(root, "rotated-value");
        File.WriteAllText(target, "linked-secret");
        File.CreateSymbolicLink(Path.Combine(tenantDirectory, "api_key"), target);

        var resolver = new MountedFileSecretResolver(root);

        Assert.Equal("linked-secret", await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("nul")]
    [InlineData("oversized")]
    [InlineData("invalid_utf8")]
    public async Task FileResolver_RejectsInvalidValuesWithoutLeakingContent(string scenario)
    {
        string path = Path.Combine(TenantDirectory("tenant-a"), "api_key");
        switch (scenario)
        {
            case "empty": File.WriteAllBytes(path, []); break;
            case "nul": File.WriteAllText(path, "do-not-leak\0value"); break;
            case "oversized": File.WriteAllBytes(path, new byte[65_537]); break;
            case "invalid_utf8": File.WriteAllBytes(path, [0xff, 0xfe]); break;
        }

        var resolver = new MountedFileSecretResolver(root);
        SecretResolutionException error = await Assert.ThrowsAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key"));

        Assert.Equal("api_key", error.SecretReference);
        Assert.Equal("file", error.ProviderName);
        Assert.DoesNotContain("do-not-leak", error.Message);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("_leading")]
    [InlineData("UPPER")]
    public async Task FileResolver_RejectsInvalidReferenceBeforeReading(string reference)
    {
        var resolver = new MountedFileSecretResolver(root);
        SecretResolutionException error = await Assert.ThrowsAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), reference));
        Assert.Equal("invalid", error.SecretReference);
        Assert.DoesNotContain(reference, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationResolver_IsolatesSameReferenceByTenant()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Secrets:tenant-a:api_key"] = "first",
            ["Secrets:tenant-b:api_key"] = "second"
        });
        var resolver = new ConfigurationSecretResolver(configuration);

        Assert.Equal("first", await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key"));
        Assert.Equal("second", await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-b"), "api_key"));
    }

    [Fact]
    public async Task ConfigurationResolver_DoesNotFallBackToFile()
    {
        WriteSecret("tenant-a", "api_key", "file-value");
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Integrios:Secrets:Provider"] = "configuration",
            ["Integrios:Secrets:FileRoot"] = root
        });
        var services = new ServiceCollection();
        services.AddIntegriosSecretResolution(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        ISecretResolver resolver = provider.GetRequiredService<ISecretResolver>();
        Assert.IsType<ConfigurationSecretResolver>(resolver);
        await Assert.ThrowsAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key"));
    }

    [Fact]
    public void DependencyInjection_DefaultsToFileAndAcceptsExplicitConfiguration()
    {
        var defaultServices = new ServiceCollection();
        defaultServices.AddIntegriosSecretResolution(Configuration([]));
        using ServiceProvider defaultProvider = defaultServices.BuildServiceProvider();
        Assert.IsType<MountedFileSecretResolver>(defaultProvider.GetRequiredService<ISecretResolver>());

        var configurationServices = new ServiceCollection();
        configurationServices.AddIntegriosSecretResolution(Configuration(new Dictionary<string, string?>
        {
            ["Integrios:Secrets:Provider"] = "configuration"
        }));
        using ServiceProvider configurationProvider = configurationServices.BuildServiceProvider();
        Assert.IsType<ConfigurationSecretResolver>(configurationProvider.GetRequiredService<ISecretResolver>());
    }

    [Fact]
    public void DependencyInjection_RejectsUnsupportedProviderAndInvalidRoot()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddIntegriosSecretResolution(
            Configuration(new Dictionary<string, string?> { ["Integrios:Secrets:Provider"] = "vault" })));
        Assert.Throws<InvalidOperationException>(() => services.AddIntegriosSecretResolution(
            Configuration(new Dictionary<string, string?>
            {
                ["Integrios:Secrets:Provider"] = "file",
                ["Integrios:Secrets:FileRoot"] = "relative"
            })));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string TenantDirectory(string slug)
    {
        string directory = Path.Combine(root, slug);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void WriteSecret(string slug, string reference, string value) =>
        File.WriteAllText(Path.Combine(TenantDirectory(slug), reference), value, new UTF8Encoding(false));

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
