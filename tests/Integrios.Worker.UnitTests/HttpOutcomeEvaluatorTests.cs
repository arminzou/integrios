using System.Text;
using Integrios.Application.Delivery;

namespace Integrios.Worker.UnitTests;

public sealed class HttpOutcomeEvaluatorTests
{
    [Fact]
    public void Evaluate_NoContract_IsAlwaysTrue()
    {
        bool accepted = HttpOutcomeEvaluator.Evaluate(null, Body("{}"), out string? diagnostic);

        Assert.True(accepted);
        Assert.Null(diagnostic);
    }

    [Fact]
    public void Evaluate_StatusCodeEvaluator_IsAlwaysTrue()
    {
        var contract = new HttpOutcomeContract { Evaluator = "status_code" };

        bool accepted = HttpOutcomeEvaluator.Evaluate(contract, Body("{\"ok\":false}"), out string? diagnostic);

        Assert.True(accepted);
        Assert.Null(diagnostic);
    }

    [Fact]
    public void Evaluate_JsonBooleanMatchesExpected_IsTrue()
    {
        var contract = new HttpOutcomeContract { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpOutcomeEvaluator.Evaluate(contract, Body("{\"ok\":true}"), out string? diagnostic);

        Assert.True(accepted);
        Assert.Null(diagnostic);
    }

    [Fact]
    public void Evaluate_JsonBooleanRejected_UsesDiagnosticFieldWhenPresent()
    {
        var contract = new HttpOutcomeContract
        {
            Evaluator = "json_boolean", Field = "ok", Expected = true, DiagnosticField = "error"
        };

        bool accepted = HttpOutcomeEvaluator.Evaluate(
            contract, Body("""{"ok":false,"error":"channel_not_found"}"""), out string? diagnostic);

        Assert.False(accepted);
        Assert.Equal("channel_not_found", diagnostic);
    }

    [Fact]
    public void Evaluate_JsonBooleanRejected_FallsBackWhenDiagnosticFieldMissing()
    {
        var contract = new HttpOutcomeContract
        {
            Evaluator = "json_boolean", Field = "ok", Expected = true, DiagnosticField = "error"
        };

        bool accepted = HttpOutcomeEvaluator.Evaluate(contract, Body("""{"ok":false}"""), out string? diagnostic);

        Assert.False(accepted);
        Assert.Contains("ok", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_MalformedJson_FailsClosed()
    {
        var contract = new HttpOutcomeContract { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpOutcomeEvaluator.Evaluate(contract, Body("not json"), out string? diagnostic);

        Assert.False(accepted);
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void Evaluate_FieldMissing_FailsClosed()
    {
        var contract = new HttpOutcomeContract { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpOutcomeEvaluator.Evaluate(contract, Body("{\"other\":1}"), out string? diagnostic);

        Assert.False(accepted);
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void Evaluate_FieldNotBoolean_FailsClosed()
    {
        var contract = new HttpOutcomeContract { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpOutcomeEvaluator.Evaluate(contract, Body("{\"ok\":\"true\"}"), out string? diagnostic);

        Assert.False(accepted);
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void Evaluate_RootNotObject_FailsClosed()
    {
        var contract = new HttpOutcomeContract { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpOutcomeEvaluator.Evaluate(contract, Body("[1,2,3]"), out string? diagnostic);

        Assert.False(accepted);
        Assert.NotNull(diagnostic);
    }

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);
}
