using System.Text;
using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Delivery;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Connectors;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Transforms;

namespace Integrios.Infrastructure.UnitTests;

public sealed class ExampleConnectorManifestTests
{
    private static readonly string[] ExpectedFiles =
        ["dataverse.json", "github.json", "http.json", "slack.json"];
    private readonly JsonataTransformEvaluator evaluator = new();

    [Fact]
    public void Examples_AreTheCurrentUnversionedSetAndParseAsOrdinaryOperatorInput()
    {
        string[] files = Directory.GetFiles(ExamplesDirectory(), "*.json")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        files.ShouldBe(ExpectedFiles);
        foreach (string file in files)
            ParseExample(file).Key.ShouldBe(Path.GetFileNameWithoutExtension(file));
    }

    [Fact]
    public void HttpExample_AcceptsAnEventSubmissionWithoutMapping()
    {
        ConnectorSourceContractManifest contract = ParseExample("http.json").SourceContracts.ShouldHaveSingleItem();
        contract.Key.ShouldBe("event_json");
        contract.Mapping.ShouldBeNull();

        SourceContractOutput output = SourceMappingOutputValidator.Validate(
            """{"event_type":"order.created","source_event_id":"order-1","payload":{"total":42}}""");

        output.EventType.ShouldBe("order.created");
        output.SourceEventId.ShouldBe("order-1");
        output.Payload.GetProperty("total").GetInt32().ShouldBe(42);
    }

    [Fact]
    public void GitHubExample_DerivesAnActionableEventTypeAndRetainsPayload()
    {
        ConnectorSourceContractManifest contract = ParseExample("github.json").SourceContracts.ShouldHaveSingleItem();
        JsonElement context = JsonSerializer.Deserialize<JsonElement>(
            """{"headers":{"x-github-event":"issues","x-github-delivery":"delivery-1"}}""");

        SourceContractOutput output = Evaluate(
            contract, """{"action":"opened","issue":{"number":42}}""", context);

        output.EventType.ShouldBe("github.issues.opened");
        output.SourceEventId.ShouldBe("delivery-1");
        output.Payload.GetProperty("issue").GetProperty("number").GetInt32().ShouldBe(42);
    }

    [Fact]
    public void GitHubExample_RejectsMissingDeliveryHeaders()
    {
        ConnectorSourceContractManifest contract = ParseExample("github.json").SourceContracts.ShouldHaveSingleItem();

        Should.Throw<TransformEvaluationException>(
            () => Evaluate(contract, "{}", JsonSerializer.Deserialize<JsonElement>("""{"headers":{}}""")));
    }

    [Fact]
    public void DataverseExample_DerivesEventTypeAndRetainsRemoteExecutionContext()
    {
        ConnectorSourceContractManifest contract = ParseExample("dataverse.json").SourceContracts.ShouldHaveSingleItem();

        SourceContractOutput output = Evaluate(
            contract,
            """{"PrimaryEntityName":"account","MessageName":"Create","OperationId":"11111111-1111-1111-1111-111111111111","Depth":1}""");

        output.EventType.ShouldBe("dataverse.account.Create");
        output.SourceEventId.ShouldBe("11111111-1111-1111-1111-111111111111");
        output.Payload.GetProperty("Depth").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void DataverseExample_RejectsAGlobalMessage()
    {
        ConnectorSourceContractManifest contract = ParseExample("dataverse.json").SourceContracts.ShouldHaveSingleItem();

        Should.Throw<TransformEvaluationException>(
            () => Evaluate(contract, """{"MessageName":"Create","OperationId":"11111111-1111-1111-1111-111111111111"}"""));
    }

    [Fact]
    public void SlackExample_RejectsAFalseSuccessResponse()
    {
        ConnectorManifest manifest = ParseExample("slack.json");
        HttpSuccessRule rule = JsonSerializer.Deserialize<HttpSuccessRule>(
            manifest.HttpSuccess!.Value, StoredJson.Options)!;

        bool accepted = HttpSuccessEvaluator.Evaluate(
            rule, Encoding.UTF8.GetBytes("""{"ok":false,"error":"channel_not_found"}"""), out string? diagnostic);

        accepted.ShouldBeFalse();
        diagnostic.ShouldBe("channel_not_found");
    }

    [Fact]
    public void ProductionProjects_DoNotLoadConnectorExamples()
    {
        string sourceDirectory = Path.Combine(RepositoryRoot(), "src");
        string[] productionFiles = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj")
            .ToArray();

        productionFiles.ShouldNotBeEmpty();
        foreach (string file in productionFiles)
            File.ReadAllText(file).ShouldNotContain("examples/connectors", Case.Insensitive);
    }

    private SourceContractOutput Evaluate(
        ConnectorSourceContractManifest contract,
        string input,
        JsonElement? context = null)
    {
        ConnectorSourceMappingManifest mapping = contract.Mapping.ShouldNotBeNull();
        string output = evaluator.Evaluate(
            new TransformSpec(mapping.Engine, mapping.Version, mapping.Expression), input, context);
        return SourceMappingOutputValidator.Validate(output);
    }

    private static ConnectorManifest ParseExample(string fileName)
    {
        string path = Path.Combine(ExamplesDirectory(), fileName);
        JsonElement document = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));

        return ConnectorManifestParser.Parse(
            document,
            new DestinationAuthenticatorRegistry([new ApiKeyHeaderAuthenticator(), new BearerTokenAuthenticator()]),
            new JsonataTransformEvaluator(),
            ConnectorManifestApplyAuthority.Operator);
    }

    private static string ExamplesDirectory() =>
        Path.Combine(RepositoryRoot(), "examples", "connectors");

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
