namespace Integrios.Application.Tenants;

public sealed class TenantValidationException(string message) : Exception(message);
