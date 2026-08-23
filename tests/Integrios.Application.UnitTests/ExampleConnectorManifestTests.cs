using System.Text.Json;
using Integrios.Application.Connectors;
using Integrios.Domain.Connectors;
using Integrios.Infrastructure.Auth;
using Integrios.Infrastructure.Connectors;

namespace Integrios.Application.UnitTests;

// Proves the public examples under examples/connectors/ are valid against the exact same
// manifest parser, real destination-authentication handler registry, and real source-adapter
// registry that Admin's PUT /admin/connectors/{key}/versions/{contractVersion} uses — i7a.8's
// "machine-validated" acceptance criterion, not a hand-rolled fake-registry approximation of it.
public sealed class ExampleConnectorManifestTests
{
    [Fact]
    public void GitHubV1Example_ParsesAsAnAuthoringSafeVerifiedWebhookSource()
    {
        ConnectorManifest manifest = ParseExample("github-v1.json");

        Assert.Equal("github", manifest.Key);
        Assert.Equal("source", manifest.Direction);
        Assert.Equal("verified_webhook", manifest.SourceAdapter?.Key);
        Assert.Equal("hmac_sha256", Assert.Single(manifest.SourceVerification.Schemes).Scheme);
        Assert.False(manifest.SourceVerification.AllowUnverified);
    }

    [Fact]
    public void SlackV1Example_ParsesAsGenericHttpBearerDestinationWithJsonBooleanOutcome()
    {
        ConnectorManifest manifest = ParseExample("slack-v1.json");

        Assert.Equal("slack", manifest.Key);
        Assert.Equal("destination", manifest.Direction);
        Assert.Equal("bearer_token", Assert.Single(manifest.DestinationAuthentication.Schemes).Scheme);
        Assert.False(manifest.DestinationAuthentication.AllowUnauthenticated);
        Assert.True(manifest.HttpOutcome.HasValue);
        Assert.Equal("json_boolean", manifest.HttpOutcome!.Value.GetProperty("evaluator").GetString());
        Assert.Equal("ok", manifest.HttpOutcome.Value.GetProperty("field").GetString());
    }

    private static ConnectorManifest ParseExample(string fileName)
    {
        string path = Path.Combine(RepositoryRoot(), "examples", "connectors", fileName);
        JsonElement document = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));

        return ConnectorManifestParser.Parse(
            document,
            new AuthSchemeRegistry([new ApiKeyHeaderAuthSchemeHandler(), new BearerTokenAuthSchemeHandler()]),
            new SourceAdapterRegistry(),
            ConnectorManifestApplyAuthority.Operator);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
