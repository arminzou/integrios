using System.Text.Json;

namespace Integrios.Application.Delivery;

// Generic logical-outcome evaluation: no provider identity, no Slack adapter, no error taxonomy.
// A provider manifest supplies field names and an expected value as data (ADR-0035); this evaluates
// them against whatever bounded body the transport layer already read.
public static class HttpOutcomeEvaluator
{
    private const int MaxDiagnosticLength = 500;

    public static bool Evaluate(HttpOutcomeContract? contract, ReadOnlySpan<byte> body, out string? diagnostic)
    {
        diagnostic = null;

        if (contract is not { Evaluator: "json_boolean" })
            return true;

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException)
        {
            diagnostic = "Response body was not valid JSON.";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(contract.Field!, out JsonElement fieldValue)
            || fieldValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            diagnostic = $"Response field '{contract.Field}' was missing or not a boolean.";
            return false;
        }

        bool actual = fieldValue.GetBoolean();
        if (actual == contract.Expected)
            return true;

        diagnostic = contract.DiagnosticField is { } diagnosticField
            && root.TryGetProperty(diagnosticField, out JsonElement diagnosticValue)
            && diagnosticValue.ValueKind == JsonValueKind.String
            && diagnosticValue.GetString() is { Length: > 0 } diagnosticText
                ? Bound(diagnosticText)
                : $"Response field '{contract.Field}' was {actual}, expected {contract.Expected}.";
        return false;
    }

    private static string Bound(string value) =>
        value.Length > MaxDiagnosticLength ? value[..MaxDiagnosticLength] : value;
}
