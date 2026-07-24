namespace Integrios.Application.Tenants;

public sealed class TenantRequestValidationException(string message) : Exception(message);
