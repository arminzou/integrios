namespace Integrios.Application.Authoring.Tenants;

public sealed class TenantValidationException(string message, string field = "")
    : AuthoringValidationException(message, field);
