namespace Integrios.Application.Authoring.TenantApiKeys;

public sealed class TenantApiKeyValidationException(string message, string field = "")
    : AuthoringValidationException(message, field);
