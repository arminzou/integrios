using System.Text.Json;

namespace Integrios.Application.Delivery;

public interface IDestinationAuthenticator
{
    string Name { get; }
    IReadOnlyList<string> RequiredConfigFields { get; }
    IReadOnlyList<string> RequiredSecretFields { get; }
    IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config);
    void Apply(
        IDictionary<string, string> headers,
        JsonElement config,
        IReadOnlyDictionary<string, string> secrets)
        => throw new NotSupportedException($"Authenticator '{Name}' requires asynchronous execution.");

    Task ApplyAsync(
        IDictionary<string, string> headers,
        JsonElement config,
        IReadOnlyDictionary<string, string> secretRefs,
        IReadOnlyDictionary<string, string> secrets,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        _ = secretRefs;
        _ = tenantId;
        _ = cancellationToken;
        Apply(headers, config, secrets);
        return Task.CompletedTask;
    }
}

public sealed class DestinationAuthenticationException(
    string message,
    int statusCode = 0,
    bool isTerminal = false,
    bool isTimeout = false,
    TimeSpan? retryAfter = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public bool IsTerminal { get; } = isTerminal;
    public bool IsTimeout { get; } = isTimeout;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
