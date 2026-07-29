namespace Integrios.Application.Secrets;

public interface ISecretResolver
{
    string ProviderName { get; }

    Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken = default);
}

public sealed record TenantSecretScope(Guid Id, string Slug);
