using Integrios.Application.Abstractions.Auth;

namespace Integrios.Infrastructure.Http.Auth;

public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public const string Prefix = "INTEGRIOS_SECRET_";

    public Task<string> ResolveAsync(Guid tenantId, string secretName, CancellationToken cancellationToken = default)
    {
        _ = tenantId;
        _ = cancellationToken;

        string variableName = Prefix + secretName.ToUpperInvariant();
        string? value = Environment.GetEnvironmentVariable(variableName);

        if (!string.IsNullOrEmpty(value))
        {
            return Task.FromResult(value);
        }

        throw new InvalidOperationException(
            $"Secret reference '{secretName}' was not found in the '{Prefix}' environment variable namespace.");
    }
}
