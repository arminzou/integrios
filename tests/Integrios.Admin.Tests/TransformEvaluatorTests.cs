using System.Text.Json;
using Integrios.Application.Transforms;
using Integrios.Infrastructure.Transforms;

namespace Integrios.Admin.Tests;

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
        Assert.Equal("payment.created", root.GetProperty("type").GetString());
        Assert.Equal(1200, root.GetProperty("amount").GetInt32());
        Assert.Equal("payments", root.GetProperty("topic").GetString());
    }

    [Fact]
    public void Evaluate_WrongPayloadPrefix_SilentlyDropsField()
    {
        // Binding-contract guard: `payload.amount` references a non-existent `payload` field
        // under the root, so JSONata yields nothing and omits the key. If the binding ever
        // changes (e.g. wrapping the payload), this test breaks loudly.
        var output = evaluator.Evaluate(Jsonata("{ \"amount\": payload.amount }"), "{\"amount\":1200}", Context);

        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.TryGetProperty("amount", out _));
    }

    [Fact]
    public void Evaluate_ExposesAllCuratedContextFields()
    {
        const string expression =
            "{ \"et\": $context.event_type, \"tn\": $context.topic_name, \"at\": $context.accepted_at }";

        var output = evaluator.Evaluate(Jsonata(expression), "{}", Context);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("payment.created", root.GetProperty("et").GetString());
        Assert.Equal("payments", root.GetProperty("tn").GetString());
        Assert.StartsWith("2026-06-01T00:00:00", root.GetProperty("at").GetString());
    }

    [Fact]
    public void Evaluate_NullTopicName_DoesNotThrow()
    {
        var context = new TransformContext("payment.created", null, Context.AcceptedAt);

        var output = evaluator.Evaluate(Jsonata("{ \"type\": $context.event_type }"), "{}", context);

        using var doc = JsonDocument.Parse(output);
        Assert.Equal("payment.created", doc.RootElement.GetProperty("type").GetString());
    }

    // --- Output shapes ---

    [Fact]
    public void Evaluate_ReturnsScalar_WhenExpressionSelectsAField()
    {
        Assert.Equal("1200", evaluator.Evaluate(Jsonata("amount"), "{\"amount\":1200}", Context));
    }

    [Fact]
    public void Evaluate_AccessesNestedPayloadFields()
    {
        Assert.Equal("\"c1\"", evaluator.Evaluate(Jsonata("customer.id"), "{\"customer\":{\"id\":\"c1\"}}", Context));
    }

    [Fact]
    public void Evaluate_OmitsKey_ForMissingOptionalField()
    {
        // The legitimate counterpart to the wrong-prefix case: referencing a genuinely absent
        // payload field is a valid pattern (optional fields), and the key is simply omitted.
        var output = evaluator.Evaluate(Jsonata("{ \"note\": memo }"), "{\"amount\":1200}", Context);

        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.TryGetProperty("note", out _));
    }

    // --- Failure paths (all surface as TransformEvaluationException, never a raw crash) ---

    [Fact]
    public void Evaluate_Throws_OnUncompilableExpression()
    {
        Assert.Throws<TransformEvaluationException>(
            () => evaluator.Evaluate(Jsonata("{ \"x\": "), "{}", Context));
    }

    [Fact]
    public void Evaluate_Throws_OnUnparseablePayload()
    {
        Assert.Throws<TransformEvaluationException>(
            () => evaluator.Evaluate(Jsonata("amount"), "not-json", Context));
    }

    [Fact]
    public void Evaluate_Throws_OnRuntimeTypeError()
    {
        // Valid syntax, but adding a number to a string is a runtime type error in JSONata.
        Assert.Throws<TransformEvaluationException>(
            () => evaluator.Evaluate(Jsonata("amount + $context.event_type"), "{\"amount\":1200}", Context));
    }

    // --- Static validation ---

    [Fact]
    public void ValidateExpression_AcceptsValid_RejectsBadInput()
    {
        Assert.Null(evaluator.ValidateExpression(Jsonata("{ \"amount\": amount }")));
        Assert.NotNull(evaluator.ValidateExpression(Jsonata("{ \"amount\": ")));  // syntax error
        Assert.NotNull(evaluator.ValidateExpression(new TransformSpec("xslt", "1", "amount")));    // unsupported engine
        Assert.NotNull(evaluator.ValidateExpression(new TransformSpec("jsonata", "2", "amount"))); // unsupported version
    }
}
