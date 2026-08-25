using System.Text.Json;
using Integrios.Application.Transforms;
using Integrios.Infrastructure.Transforms;

namespace Integrios.Infrastructure.UnitTests;

// Pure unit tests for the JSONata evaluator — no database, no Testcontainers.
public class TransformEvaluatorTests
{
    private static readonly TransformContext Context =
        new("payment.created", "payments", DateTimeOffset.Parse("2026-06-01T00:00:00Z"));

    private readonly JsonataTransformEvaluator evaluator = new();

    private static TransformSpec Jsonata(string expression) => new("jsonata", "1", expression);

    // --- Binding contract ---

    [Fact]
    public void Evaluate_BindsPayloadAsRoot_AndContextAsVariable()
    {
        // The payload is the JSONata root (bare `amount`); platform metadata is `$context`.
        const string expression =
            "{ \"type\": $context.event_type, \"amount\": amount, \"topic\": $context.topic_name }";

        var output = evaluator.Evaluate(Jsonata(expression), "{\"amount\":1200,\"paymentId\":\"pay_001\"}", Context);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().ShouldBe("payment.created");
        root.GetProperty("amount").GetInt32().ShouldBe(1200);
        root.GetProperty("topic").GetString().ShouldBe("payments");
    }

    [Fact]
    public void Evaluate_WrongPayloadPrefix_SilentlyDropsField()
    {
        // Binding-contract guard: `payload.amount` references a non-existent `payload` field
        // under the root, so JSONata yields nothing and omits the key. If the binding ever
        // changes (e.g. wrapping the payload), this test breaks loudly.
        var output = evaluator.Evaluate(Jsonata("{ \"amount\": payload.amount }"), "{\"amount\":1200}", Context);

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.TryGetProperty("amount", out _).ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_ExposesAllCuratedContextFields()
    {
        const string expression =
            "{ \"et\": $context.event_type, \"tn\": $context.topic_name, \"at\": $context.accepted_at }";

        var output = evaluator.Evaluate(Jsonata(expression), "{}", Context);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        root.GetProperty("et").GetString().ShouldBe("payment.created");
        root.GetProperty("tn").GetString().ShouldBe("payments");
        root.GetProperty("at").GetString()!.ShouldStartWith("2026-06-01T00:00:00", Case.Sensitive);
    }

    [Fact]
    public void Evaluate_NullTopicName_DoesNotThrow()
    {
        var context = new TransformContext("payment.created", null, Context.AcceptedAt);

        var output = evaluator.Evaluate(Jsonata("{ \"type\": $context.event_type }"), "{}", context);

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("payment.created");
    }

    // --- Output shapes ---

    [Fact]
    public void Evaluate_ReturnsScalar_WhenExpressionSelectsAField()
    {
        evaluator.Evaluate(Jsonata("amount"), "{\"amount\":1200}", Context).ShouldBe("1200");
    }

    [Fact]
    public void Evaluate_AccessesNestedPayloadFields()
    {
        evaluator.Evaluate(Jsonata("customer.id"), "{\"customer\":{\"id\":\"c1\"}}", Context).ShouldBe("\"c1\"");
    }

    [Fact]
    public void Evaluate_OmitsKey_ForMissingOptionalField()
    {
        // The legitimate counterpart to the wrong-prefix case: referencing a genuinely absent
        // payload field is a valid pattern (optional fields), and the key is simply omitted.
        var output = evaluator.Evaluate(Jsonata("{ \"note\": memo }"), "{\"amount\":1200}", Context);

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.TryGetProperty("note", out _).ShouldBeFalse();
    }

    // --- Failure paths (all surface as TransformEvaluationException, never a raw crash) ---

    [Fact]
    public void Evaluate_Throws_OnUncompilableExpression()
    {
        Should.Throw<TransformEvaluationException>(
            () => evaluator.Evaluate(Jsonata("{ \"x\": "), "{}", Context));
    }

    [Fact]
    public void Evaluate_Throws_OnUnparseablePayload()
    {
        Should.Throw<TransformEvaluationException>(
            () => evaluator.Evaluate(Jsonata("amount"), "not-json", Context));
    }

    [Fact]
    public void Evaluate_Throws_OnRuntimeTypeError()
    {
        // Valid syntax, but adding a number to a string is a runtime type error in JSONata.
        Should.Throw<TransformEvaluationException>(
            () => evaluator.Evaluate(Jsonata("amount + $context.event_type"), "{\"amount\":1200}", Context));
    }

    // --- Static validation ---

    [Fact]
    public void ValidateExpression_AcceptsValid_RejectsBadInput()
    {
        evaluator.ValidateExpression(Jsonata("{ \"amount\": amount }")).ShouldBeNull();
        evaluator.ValidateExpression(Jsonata("{ \"amount\": ")).ShouldNotBeNull();  // syntax error
        evaluator.ValidateExpression(new TransformSpec("xslt", "1", "amount")).ShouldNotBeNull();    // unsupported engine
        evaluator.ValidateExpression(new TransformSpec("jsonata", "2", "amount")).ShouldNotBeNull(); // unsupported version
    }

    // Proves the exact expression published in docs/github-to-slack-walkthrough.md against a
    // realistic GitHub push payload shape, so the walkthrough's transform is mechanically checked
    // rather than merely eyeballed.
    [Fact]
    public void Evaluate_GitHubToSlackWalkthroughTransform_ProducesTheDocumentedSlackMessage()
    {
        const string expression =
            "{'channel': '#deploys', 'text': pusher.name & ' pushed to ' & repository.full_name & ': ' & head_commit.message}";
        const string githubPushPayload = """
            {
              "pusher": { "name": "octocat" },
              "repository": { "full_name": "acme/widgets" },
              "head_commit": { "message": "fix: correct off-by-one in retry backoff" }
            }
            """;

        var output = evaluator.Evaluate(Jsonata(expression), githubPushPayload, Context);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        root.GetProperty("channel").GetString().ShouldBe("#deploys");
        root.GetProperty("text").GetString().ShouldBe(
            "octocat pushed to acme/widgets: fix: correct off-by-one in retry backoff");
    }
}
