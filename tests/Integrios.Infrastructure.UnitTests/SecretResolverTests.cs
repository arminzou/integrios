using System.Text;
using Integrios.Application.Secrets;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Infrastructure.UnitTests;

public sealed class SecretResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"integrios-secrets-{Guid.NewGuid():N}");

    public SecretResolverTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task FileResolver_IsolatesSameReferenceByTenantAndTrimsEdgeLineBreaks()
    {
        WriteSecret("tenant-a", "api_key", "first\n");
        WriteSecret("tenant-b", "api_key", "second");
        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);

        string tenantA = await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None);
        string tenantB = await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-b"), "api_key", CancellationToken.None);

        tenantA.ShouldBe("first");
        tenantB.ShouldBe("second");
    }

    // No real credential is defined to include an edge CR/LF (HTTP headers can't carry raw CRLF
    // at all), so a trailing newline here is always a storage/editor artifact, never a legitimate
    // byte of the secret. Trimming only the edges — never the interior — means a genuinely
    // corrupted value with an embedded line break still fails loud downstream.
    [Fact]
    public async Task FileResolver_TrimsEdgeCarriageReturnAndLineFeed()
    {
        WriteSecret("tenant-a", "api_key", "\r\nvalue\r\n");
        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);

        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None)).ShouldBe(
            "value");
    }

    [Fact]
    public async Task FileResolver_RejectsAValueThatIsOnlyLineBreaksAfterTrimming()
    {
        WriteSecret("tenant-a", "api_key", "\n");
        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);

        await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None));
    }

    [Fact]
    public async Task ConfigurationResolver_TrimsEdgeLineBreaks()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["DestinationSecrets:tenant-a:api_key"] = "value\n"
        });
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None)).ShouldBe(
            "value");
    }

    [Fact]
    public async Task FileResolver_ReadsEveryLookupSoRotationIsVisible()
    {
        WriteSecret("tenant-a", "api_key", "before");
        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);
        TenantSecretScope tenant = new(Guid.NewGuid(), "tenant-a");

        (await resolver.ResolveAsync(tenant, "api_key", CancellationToken.None)).ShouldBe("before");
        WriteSecret("tenant-a", "api_key", "after");
        (await resolver.ResolveAsync(tenant, "api_key", CancellationToken.None)).ShouldBe("after");
    }

    [Fact]
    public async Task FileResolver_FollowsSymlinks()
    {
        string tenantDirectory = TenantDirectory("tenant-a");
        string target = Path.Combine(root, "rotated-value");
        File.WriteAllText(target, "linked-secret");
        File.CreateSymbolicLink(Path.Combine(tenantDirectory, "api_key"), target);

        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);

        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None)).ShouldBe("linked-secret");
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
        SecretResolutionException error = await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None));

        error.SecretReference.ShouldBe("api_key");
        error.ProviderName.ShouldBe("file");
        error.Message.ShouldNotContain("do-not-leak", Case.Sensitive);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("_leading")]
    [InlineData("UPPER")]
    public async Task FileResolver_RejectsInvalidReferenceBeforeReading(string reference)
    {
        var resolver = new DestinationAuthenticationMountedFileSecretResolver(root);
        SecretResolutionException error = await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), reference, CancellationToken.None));
        error.SecretReference.ShouldBe("invalid");
        error.Message.ShouldNotContain(reference, Case.Sensitive);
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

        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None)).ShouldBe("first");
        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-b"), "api_key", CancellationToken.None)).ShouldBe("second");
    }

    [Fact]
    public async Task ConfigurationResolver_DoesNotReadTheLegacyUnqualifiedNamespace()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Secrets:tenant-a:api_key"] = "legacy-value"
        });
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None));
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
        resolver.ShouldBeOfType<DestinationAuthenticationConfigurationSecretResolver>();
        await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "api_key", CancellationToken.None));
    }

    [Fact]
    public void DependencyInjection_UsesFileAndAcceptsExplicitConfiguration()
    {
        Path.IsPathFullyQualified(DestinationAuthenticationMountedFileSecretResolver.DefaultRoot).ShouldBeTrue();
        Path.IsPathFullyQualified(SourceVerificationMountedFileSecretResolver.DefaultRoot).ShouldBeTrue();
        string expectedDestinationRoot = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Integrios",
                "secrets",
                "destination")
            : "/run/secrets/integrios/destination";
        string expectedSourceRoot = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Integrios",
                "secrets",
                "source")
            : "/run/secrets/integrios/source";
        DestinationAuthenticationMountedFileSecretResolver.DefaultRoot.ShouldBe(expectedDestinationRoot);
        SourceVerificationMountedFileSecretResolver.DefaultRoot.ShouldBe(expectedSourceRoot);

        var defaultServices = new ServiceCollection();
        defaultServices.AddDestinationAuthenticationSecretResolutionServices(Configuration(
            new Dictionary<string, string?> { ["Integrios:DestinationSecrets:FileRoot"] = root }));
        using ServiceProvider defaultProvider = defaultServices.BuildServiceProvider();
        defaultProvider.GetRequiredService<IDestinationAuthenticationSecretResolver>()
            .ShouldBeOfType<DestinationAuthenticationMountedFileSecretResolver>();

        var configurationServices = new ServiceCollection();
        configurationServices.AddDestinationAuthenticationSecretResolutionServices(Configuration(new Dictionary<string, string?>
        {
            ["Integrios:DestinationSecrets:Provider"] = "configuration"
        }));
        using ServiceProvider configurationProvider = configurationServices.BuildServiceProvider();
        configurationProvider.GetRequiredService<IDestinationAuthenticationSecretResolver>()
            .ShouldBeOfType<DestinationAuthenticationConfigurationSecretResolver>();
    }

    [Fact]
    public void DependencyInjection_RejectsUnsupportedProviderAndInvalidRoot()
    {
        var services = new ServiceCollection();
        Should.Throw<InvalidOperationException>(() => services.AddDestinationAuthenticationSecretResolutionServices(
            Configuration(new Dictionary<string, string?> { ["Integrios:DestinationSecrets:Provider"] = "vault" })));
        Should.Throw<InvalidOperationException>(() => services.AddDestinationAuthenticationSecretResolutionServices(
            Configuration(new Dictionary<string, string?>
            {
                ["Integrios:DestinationSecrets:Provider"] = "file",
                ["Integrios:DestinationSecrets:FileRoot"] = "relative"
            })));
        Should.Throw<InvalidOperationException>(() => services.AddDestinationAuthenticationSecretResolutionServices(
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

        (await resolver.ResolveAsync(
            new TenantSecretScope(Guid.NewGuid(), "tenant-a"),
            "webhook_secret",
            CancellationToken.None)).ShouldBe("source-value");
        provider.GetService<IDestinationAuthenticationSecretResolver>().ShouldBeNull();
    }

    [Fact]
    public async Task SourceVerificationFile_UsesTheSourcePortAndSharedFileBehavior()
    {
        WriteSecret("tenant-a", "webhook_secret", "source-file-value");
        var resolver = new SourceVerificationMountedFileSecretResolver(root);

        string value = await resolver.ResolveAsync(
            new TenantSecretScope(Guid.NewGuid(), "tenant-a"),
            "webhook_secret",
            CancellationToken.None);

        value.ShouldBe("source-file-value");
        resolver.ShouldBeAssignableTo<ISourceVerificationSecretResolver>();
        resolver.ShouldNotBeAssignableTo<IDestinationAuthenticationSecretResolver>();
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
