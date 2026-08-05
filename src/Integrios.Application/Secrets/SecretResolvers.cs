namespace Integrios.Application.Secrets;

public interface IDestinationAuthenticationSecretResolver
{
    string ProviderName { get; }

    Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken);
}

public interface ISourceVerificationSecretResolver
{
    string ProviderName { get; }

    Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken);
}

public sealed record TenantSecretScope(Guid Id, string Slug);
