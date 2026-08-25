using Integrios.Application.Bootstrap;
using Integrios.Application.Transforms;
using Integrios.Infrastructure.Transforms;

namespace Integrios.Infrastructure.UnitTests;

// Proves the built-in Dataverse queue Source mapping (voe.7's second required example contract)
// actually compiles and evaluates against the real JSONata engine, not just that its string parses.
public sealed class DataverseSourceMappingTests
{
    private static readonly TransformSpec Mapping = new("jsonata", "1", BuiltinCatalog.RemoteExecutionContextMapping);
    private readonly JsonataTransformEvaluator evaluator = new();

    [Fact]
    public void Evaluate_ValidContext_DerivesBoundedEventTypeAndRetainsFullPayload()
    {
        const string input = """
            {"PrimaryEntityName":"account","MessageName":"Create","OperationId":"11111111-1111-1111-1111-111111111111","Depth":1}
            """;

        string outputJson = evaluator.Evaluate(Mapping, input, (System.Text.Json.JsonElement?)null);
        SourceContractOutput output = SourceMappingOutputValidator.Validate(outputJson);

        output.EventType.ShouldBe("dataverse.account.Create");
        output.SourceEventId.ShouldBe("11111111-1111-1111-1111-111111111111");
        output.Payload.GetProperty("Depth").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Evaluate_GlobalMessageWithNoPrimaryEntity_Throws()
    {
        const string input = """{"MessageName":"Create","OperationId":"11111111-1111-1111-1111-111111111111"}""";

        Should.Throw<TransformEvaluationException>(
            () => evaluator.Evaluate(Mapping, input, (System.Text.Json.JsonElement?)null));
    }

    [Fact]
    public void Evaluate_EmptyPrimaryEntityName_Throws()
    {
        const string input = """{"PrimaryEntityName":"","MessageName":"Create","OperationId":"11111111-1111-1111-1111-111111111111"}""";

        Should.Throw<TransformEvaluationException>(
            () => evaluator.Evaluate(Mapping, input, (System.Text.Json.JsonElement?)null));
    }

    [Fact]
    public void Evaluate_MissingOperationId_Throws()
    {
        const string input = """{"PrimaryEntityName":"account","MessageName":"Create"}""";

        Should.Throw<TransformEvaluationException>(
            () => evaluator.Evaluate(Mapping, input, (System.Text.Json.JsonElement?)null));
    }
}
