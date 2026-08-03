using System.Text;
using Integrios.Application.Secrets;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Secrets;
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
        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);

        string tenantA = await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key");
        string tenantB = await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-b"), "api_key");

        Assert.Equal("first\n", tenantA);
        Assert.Equal("second", tenantB);
    }

    [Fact]
    public async Task FileResolver_ReadsEveryLookupSoRotationIsVisible()
    {
        WriteSecret("tenant-a", "api_key", "before");
        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);
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

        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);

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

        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);
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
        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);
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
            ["DestinationSecrets:tenant-a:api_key"] = "first",
            ["DestinationSecrets:tenant-b:api_key"] = "second"
        });
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        Assert.Equal("first", await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key"));
        Assert.Equal("second", await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-b"), "api_key"));
    }

    [Fact]
    public async Task ConfigurationResolver_DoesNotReadTheLegacyUnqualifiedNamespace()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Secrets:tenant-a:api_key"] = "legacy-value"
        });
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        await Assert.ThrowsAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key"));
    }

    [Fact]
    public async Task ConfigurationResolver_DoesNotFallBackToFile()
    {
        WriteSecret("tenant-a", "api_key", "file-value");
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Integrios:DestinationSecrets:Provider"] = "configuration",
            ["Integrios:DestinationSecrets:FileRoot"] = root
        });
        var services = new ServiceCollection();
        services.AddDestinationAuthenticationSecretResolutionServices(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        IDestinationAuthenticationSecretResolver resolver = provider.GetRequiredService<IDestinationAuthenticationSecretResolver>();
        Assert.IsType<DestinationAuthenticationConfigurationSecretResolver>(resolver);
        await Assert.ThrowsAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key"));
    }

    [Fact]
    public void DependencyInjection_UsesFileAndAcceptsExplicitConfiguration()
    {
        Assert.True(Path.IsPathFullyQualified(DestinationAuthenticationMountedFileSecretResolver.DefaultRoot));
        Assert.True(Path.IsPathFullyQualified(SourceVerificationMountedFileSecretResolver.DefaultRoot));
        Assert.EndsWith(
            Path.Combine("secrets", "destination"),
            DestinationAuthenticationMountedFileSecretResolver.DefaultRoot,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("secrets", "source"),
            SourceVerificationMountedFileSecretResolver.DefaultRoot,
            StringComparison.OrdinalIgnoreCase);

        var defaultServices = new ServiceCollection();
        defaultServices.AddDestinationAuthenticationSecretResolutionServices(Configuration(
            new Dictionary<string, string?> { ["Integrios:DestinationSecrets:FileRoot"] = root }));
        using ServiceProvider defaultProvider = defaultServices.BuildServiceProvider();
        Assert.IsType<DestinationAuthenticationMountedFileSecretResolver>(
            defaultProvider.GetRequiredService<IDestinationAuthenticationSecretResolver>());

        var configurationServices = new ServiceCollection();
        configurationServices.AddDestinationAuthenticationSecretResolutionServices(Configuration(new Dictionary<string, string?>
        {
            ["Integrios:DestinationSecrets:Provider"] = "configuration"
        }));
        using ServiceProvider configurationProvider = configurationServices.BuildServiceProvider();
        Assert.IsType<DestinationAuthenticationConfigurationSecretResolver>(
            configurationProvider.GetRequiredService<IDestinationAuthenticationSecretResolver>());
    }

    [Fact]
    public void DependencyInjection_RejectsUnsupportedProviderAndInvalidRoot()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddDestinationAuthenticationSecretResolutionServices(
            Configuration(new Dictionary<string, string?> { ["Integrios:DestinationSecrets:Provider"] = "vault" })));
        Assert.Throws<InvalidOperationException>(() => services.AddDestinationAuthenticationSecretResolutionServices(
            Configuration(new Dictionary<string, string?>
            {
                ["Integrios:DestinationSecrets:Provider"] = "file",
                ["Integrios:DestinationSecrets:FileRoot"] = "relative"
            })));
        Assert.Throws<InvalidOperationException>(() => services.AddDestinationAuthenticationSecretResolutionServices(
            Configuration(new Dictionary<string, string?>
            {
                ["Integrios:DestinationSecrets:Provider"] = "file",
                ["Integrios:DestinationSecrets:FileRoot"] = Path.Combine(root, "missing")
            })));
    }

    [Fact]
    public async Task SourceVerificationConfiguration_UsesAnIsolatedNamespaceAndPort()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Integrios:SourceSecrets:Provider"] = "configuration",
            ["SourceSecrets:tenant-a:webhook_secret"] = "source-value",
            ["DestinationSecrets:tenant-a:webhook_secret"] = "destination-value",
        });
        var services = new ServiceCollection();
        services.AddSourceVerificationSecretResolutionServices(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        ISourceVerificationSecretResolver resolver =
            provider.GetRequiredService<ISourceVerificationSecretResolver>();

        Assert.Equal("source-value", await resolver.ResolveAsync(
            new TenantSecretScope(Guid.NewGuid(), "tenant-a"),
            "webhook_secret"));
        Assert.Null(provider.GetService<IDestinationAuthenticationSecretResolver>());
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
