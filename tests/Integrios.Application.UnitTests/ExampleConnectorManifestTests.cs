using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Connectors;
using Integrios.Infrastructure.Transforms;

namespace Integrios.Application.UnitTests;

// Proves the public examples under examples/connectors/ are valid against the exact same
// manifest parser, real destination-authentication handler registry, and real source-contract
// registry that Admin's PUT /admin/connectors/{key}/versions/{contractVersion} uses — i7a.8's
// "machine-validated" acceptance criterion, not a hand-rolled fake-registry approximation of it.
public sealed class ExampleConnectorManifestTests
{
    [Fact]
    public void GitHubV1Example_ParsesAsAnAuthoringSafeVerifiedWebhookSource()
    {
        ConnectorManifest manifest = ParseExample("github-v1.json");

        manifest.Key.ShouldBe("github");
        manifest.Direction.ShouldBe("source");
        manifest.SourceContracts.ShouldHaveSingleItem().Key.ShouldBe("verified_webhook");
        manifest.SourceVerification.Schemes.ShouldHaveSingleItem().Scheme.ShouldBe("hmac_sha256");
        manifest.SourceVerification.AllowUnverified.ShouldBeFalse();
    }

    [Fact]
    public void SlackV1Example_ParsesAsGenericHttpBearerDestinationWithJsonBooleanOutcome()
    {
        ConnectorManifest manifest = ParseExample("slack-v1.json");

        manifest.Key.ShouldBe("slack");
        manifest.Direction.ShouldBe("destination");
        manifest.DestinationAuthentication.Schemes.ShouldHaveSingleItem().Scheme.ShouldBe("bearer_token");
        manifest.DestinationAuthentication.AllowUnauthenticated.ShouldBeFalse();
        manifest.HttpSuccess.HasValue.ShouldBeTrue();
        manifest.HttpSuccess!.Value.GetProperty("evaluator").GetString().ShouldBe("json_boolean");
        manifest.HttpSuccess.Value.GetProperty("field").GetString().ShouldBe("ok");
    }

    private static ConnectorManifest ParseExample(string fileName)
    {
        string path = Path.Combine(RepositoryRoot(), "examples", "connectors", fileName);
        JsonElement document = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));

        return ConnectorManifestParser.Parse(
            document,
            new DestinationAuthenticatorRegistry([new ApiKeyHeaderAuthenticator(), new BearerTokenAuthenticator()]),
            new JsonataTransformEvaluator(),
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
