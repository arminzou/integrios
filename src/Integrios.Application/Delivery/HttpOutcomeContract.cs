namespace Integrios.Application.Delivery;

// Mirrors the manifest's already-validated http_outcome shape (ConnectorManifestParser owns
// authoring validation); dispatch only ever reads an already-validated snapshot, so this record
// carries no parsing beyond ordinary JSON deserialization. Property names match the manifest's
// snake_case fields exactly under ConnectionSchemeSelection.StoredJson's naming policy.
public sealed record HttpOutcomeContract
{
    public const int DefaultMaxBodyBytes = 65_536;

    public required string Evaluator { get; init; }
    public string? Field { get; init; }
    public bool? Expected { get; init; }
    public string? DiagnosticField { get; init; }
    public int? MaxBodyBytes { get; init; }
}
