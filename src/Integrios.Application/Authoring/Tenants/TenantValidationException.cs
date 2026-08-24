namespace Integrios.Application.Authoring.Tenants;

public sealed class TenantValidationException(string message) : Exception(message);
