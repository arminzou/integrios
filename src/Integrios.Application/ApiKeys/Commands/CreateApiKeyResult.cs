namespace Integrios.Application.ApiKeys;

// Returned only on create — carries the plaintext token once.
public sealed record CreateApiKeyResult
{
    public required ApiKeyDto ApiKey { get; init; }
    public required string Token { get; init; }
}
