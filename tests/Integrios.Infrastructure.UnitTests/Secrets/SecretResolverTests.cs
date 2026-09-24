using System.Text;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;
using Integrios.Application.Secrets;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.KeyPerFile;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Infrastructure.UnitTests;

public sealed class SecretResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"integrios-secrets-{Guid.NewGuid():N}");

    public SecretResolverTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task ConfigurationResolver_ResolvesFromEnvironmentVariable()
    {
        string slug = $"env-{Guid.NewGuid():N}";
        string variable = $"DestinationSecrets__{slug}__token";
        Environment.SetEnvironmentVariable(variable, "environment-value");
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

            (await resolver.ResolveAsync(new(Guid.NewGuid(), slug), "token", CancellationToken.None))
                .ShouldBe("environment-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // Key-per-file strips at most one trailing Environment.NewLine, so "\n" survives it on Windows
    // and "\r\n" survives it on Linux; the resolver's edge CR/LF trim covers both.
    [Fact]
    public async Task ConfigurationResolver_ResolvesFromKeyPerFileWithoutTrailingLineBreaks()
    {
        WriteFile("DestinationSecrets__tenant-a__token", "first\n");
        WriteFile("DestinationSecrets__tenant-b__token", "second\r\n");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddKeyPerFile(root, optional: true, reloadOnChange: false)
            .Build();
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "token", CancellationToken.None)).ShouldBe("first");
        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-b"), "token", CancellationToken.None)).ShouldBe("second");
    }

    [Fact]
    public async Task ConfigurationResolver_ResolvesAKeyVaultSecretNameThroughTheStockKeyMapping()
    {
        string key = new KeyVaultSecretManager().GetKey(
            new KeyVaultSecret("DestinationSecrets--tenant-a--token", "vault-value"));
        IConfiguration configuration = Configuration(new Dictionary<string, string?> { [key] = "vault-value" });
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "token", CancellationToken.None))
            .ShouldBe("vault-value");
    }

    [Fact]
    public async Task ConfigurationResolver_TrimsEdgeLineBreaks()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["DestinationSecrets:tenant-a:token"] = "\r\nvalue\r\n"
        });
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "token", CancellationToken.None)).ShouldBe(
            "value");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("line_breaks_only")]
    [InlineData("nul")]
    [InlineData("oversized")]
    [InlineData("lone_surrogate")]
    [InlineData("undecodable_bytes")]
    public async Task ConfigurationResolver_RejectsMissingOrInvalidValuesWithoutLeakingContent(string scenario)
    {
        string? value = scenario switch
        {
            "empty" => "",
            "line_breaks_only" => "\n",
            "nul" => "do-not-leak\0value",
            "oversized" => new string('a', 65_537),
            "lone_surrogate" => "do-not-leak\ud800",
            "undecodable_bytes" => "do-not-leak�value",
            _ => null
        };
        var values = new Dictionary<string, string?>();
        if (value is not null)
            values["DestinationSecrets:tenant-a:token"] = value;
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(Configuration(values));

        SecretResolutionException error = await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "token", CancellationToken.None));

        error.SecretReference.ShouldBe("token");
        error.ProviderName.ShouldBe("configuration");
        error.Message.ShouldNotContain("do-not-leak", Case.Sensitive);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("UPPER")]
    public async Task ConfigurationResolver_RejectsInvalidReferenceBeforeReading(string reference)
    {
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(Configuration([]));
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
            ["DestinationSecrets:tenant-a:token"] = "first",
            ["DestinationSecrets:tenant-b:token"] = "second"
        });
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "token", CancellationToken.None)).ShouldBe("first");
        (await resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-b"), "token", CancellationToken.None)).ShouldBe("second");
    }

    [Fact]
    public async Task ConfigurationResolver_DoesNotReadTheLegacyUnqualifiedNamespace()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["Secrets:tenant-a:token"] = "legacy-value"
        });
        var resolver = new DestinationAuthenticationConfigurationSecretResolver(configuration);

        await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(new(Guid.NewGuid(), "tenant-a"), "token", CancellationToken.None));
    }

    [Fact]
    public async Task WorkerResolvesNoSourceSecretAndIngestionNoDestinationSecret()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["SourceSecrets:tenant-a:source"] = "source-value",
            ["DestinationSecrets:tenant-a:destination"] = "destination-value",
        });
        TenantSecretScope tenant = new(Guid.NewGuid(), "tenant-a");

        var worker = new ServiceCollection();
        worker.AddDestinationAuthenticationSecretResolutionServices(configuration);
        using ServiceProvider workerProvider = worker.BuildServiceProvider();
        IDestinationAuthenticationSecretResolver destination =
            workerProvider.GetRequiredService<IDestinationAuthenticationSecretResolver>();
        workerProvider.GetService<ISourceVerificationSecretResolver>().ShouldBeNull();

        var ingestion = new ServiceCollection();
        ingestion.AddSourceVerificationSecretResolutionServices(configuration);
        using ServiceProvider ingestionProvider = ingestion.BuildServiceProvider();
        ISourceVerificationSecretResolver source =
            ingestionProvider.GetRequiredService<ISourceVerificationSecretResolver>();
        ingestionProvider.GetService<IDestinationAuthenticationSecretResolver>().ShouldBeNull();

        (await destination.ResolveAsync(tenant, "destination", CancellationToken.None)).ShouldBe("destination-value");
        await Should.ThrowAsync<SecretResolutionException>(
            () => destination.ResolveAsync(tenant, "source", CancellationToken.None));
        (await source.ResolveAsync(tenant, "source", CancellationToken.None)).ShouldBe("source-value");
        await Should.ThrowAsync<SecretResolutionException>(
            () => source.ResolveAsync(tenant, "destination", CancellationToken.None));
    }

    [Fact]
    public void SecretConfigurationSources_WithoutAVaultAddOnlyOptionalKeyPerFileAndStart()
    {
        using var configuration = new ConfigurationManager();

        configuration.AddSecretConfigurationSources();

        KeyPerFileConfigurationSource keyPerFile =
            configuration.Sources.OfType<KeyPerFileConfigurationSource>().ShouldHaveSingleItem();
        keyPerFile.Optional.ShouldBeTrue();
        keyPerFile.ReloadOnChange.ShouldBeFalse();
        configuration.Sources.ShouldNotContain(source => source.GetType().Name.Contains("KeyVault"));
    }

    [Fact]
    public void SecretConfigurationSources_UnreachableVaultFailsStartup()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrios:KeyVault:Uri"] = "https://integrios-unreachable.invalid/"
        });

        Should.Throw<Exception>(() => configuration.AddSecretConfigurationSources());
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void WriteFile(string name, string value) =>
        File.WriteAllText(Path.Combine(root, name), value, new UTF8Encoding(false));

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
