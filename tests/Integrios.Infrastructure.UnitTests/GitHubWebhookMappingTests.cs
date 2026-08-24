using System.Text.Json;
using Integrios.Application.Bootstrap;
using Integrios.Application.Transforms;
using Integrios.Infrastructure.Transforms;

namespace Integrios.Infrastructure.UnitTests;

// Proves the built-in GitHub webhook Source mapping against the real JSONata engine, using the
// same lower-cased $context.headers shape AcceptVerifiedWebhookCommand actually builds.
public sealed class GitHubWebhookMappingTests
{
    private static readonly TransformSpec Mapping = new("jsonata", "1", BuiltinCatalog.GitHubWebhookMapping);
    private readonly JsonataTransformEvaluator evaluator = new();

    [Fact]
    public void Evaluate_PushEvent_DerivesEventTypeAndRetainsPayload()
    {
        const string input = """{"pusher":{"name":"octocat"},"repository":{"full_name":"acme/widgets"}}""";
        JsonElement context = JsonSerializer.Deserialize<JsonElement>(
            """{"headers":{"x-github-event":"push","x-github-delivery":"delivery-1"}}""");

        string outputJson = evaluator.Evaluate(Mapping, input, context);
        SourceContractOutput output = SourceMappingOutputValidator.Validate(outputJson);

        Assert.Equal("github.push", output.EventType);
        Assert.Equal("delivery-1", output.SourceEventId);
        Assert.Equal("octocat", output.Payload.GetProperty("pusher").GetProperty("name").GetString());
    }
}
