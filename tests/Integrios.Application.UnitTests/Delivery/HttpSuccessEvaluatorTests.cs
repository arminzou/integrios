using System.Text;
using Integrios.Application.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class HttpSuccessEvaluatorTests
{
    [Fact]
    public void Evaluate_NoContract_IsAlwaysTrue()
    {
        bool accepted = HttpSuccessEvaluator.Evaluate(null, Body("{}"), out string? diagnostic);

        accepted.ShouldBeTrue();
        diagnostic.ShouldBeNull();
    }

    [Fact]
    public void Evaluate_StatusCodeEvaluator_IsAlwaysTrue()
    {
        var contract = new HttpSuccessRule { Evaluator = "status_code" };

        bool accepted = HttpSuccessEvaluator.Evaluate(contract, Body("{\"ok\":false}"), out string? diagnostic);

        accepted.ShouldBeTrue();
        diagnostic.ShouldBeNull();
    }

    [Fact]
    public void Evaluate_JsonBooleanMatchesExpected_IsTrue()
    {
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpSuccessEvaluator.Evaluate(contract, Body("{\"ok\":true}"), out string? diagnostic);

        accepted.ShouldBeTrue();
        diagnostic.ShouldBeNull();
    }

    [Fact]
    public void Evaluate_JsonBooleanRejected_UsesDiagnosticFieldWhenPresent()
    {
        var contract = new HttpSuccessRule
        {
            Evaluator = "json_boolean", Field = "ok", Expected = true, DiagnosticField = "error"
        };

        bool accepted = HttpSuccessEvaluator.Evaluate(
            contract, Body("""{"ok":false,"error":"channel_not_found"}"""), out string? diagnostic);

        accepted.ShouldBeFalse();
        diagnostic.ShouldBe("channel_not_found");
    }

    [Fact]
    public void Evaluate_JsonBooleanRejected_FallsBackWhenDiagnosticFieldMissing()
    {
        var contract = new HttpSuccessRule
        {
            Evaluator = "json_boolean", Field = "ok", Expected = true, DiagnosticField = "error"
        };

        bool accepted = HttpSuccessEvaluator.Evaluate(contract, Body("""{"ok":false}"""), out string? diagnostic);

        accepted.ShouldBeFalse();
        diagnostic!.ShouldContain("ok", Case.Sensitive);
    }

    [Fact]
    public void Evaluate_MalformedJson_FailsClosed()
    {
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpSuccessEvaluator.Evaluate(contract, Body("not json"), out string? diagnostic);

        accepted.ShouldBeFalse();
        diagnostic.ShouldNotBeNull();
    }

    [Fact]
    public void Evaluate_FieldMissing_FailsClosed()
    {
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpSuccessEvaluator.Evaluate(contract, Body("{\"other\":1}"), out string? diagnostic);

        accepted.ShouldBeFalse();
        diagnostic.ShouldNotBeNull();
    }

    [Fact]
    public void Evaluate_FieldNotBoolean_FailsClosed()
    {
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpSuccessEvaluator.Evaluate(contract, Body("{\"ok\":\"true\"}"), out string? diagnostic);

        accepted.ShouldBeFalse();
        diagnostic.ShouldNotBeNull();
    }

    [Fact]
    public void Evaluate_RootNotObject_FailsClosed()
    {
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        bool accepted = HttpSuccessEvaluator.Evaluate(contract, Body("[1,2,3]"), out string? diagnostic);

        accepted.ShouldBeFalse();
        diagnostic.ShouldNotBeNull();
    }

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);
}
