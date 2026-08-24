namespace Integrios.Application.Authoring.TenantApiKeys;

// Returned only on create — carries the plaintext token once.
public sealed record CreateTenantApiKeyResult
{
    public required TenantApiKeyDto TenantApiKey { get; init; }
    public required string Token { get; init; }
}
