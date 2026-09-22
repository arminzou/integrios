using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Secrets;

namespace Integrios.Infrastructure.Delivery;

internal sealed class OAuth2ClientCredentialsAuthenticator(HttpClient? httpClient, TimeProvider timeProvider)
    : IDestinationAuthenticator
{
    internal const int ResponseMaxBytes = 64 * 1024;
    private readonly ConcurrentDictionary<TokenCacheKey, Lazy<Task<CachedToken>>> _tokens = [];

    public string Name => "oauth2_client_credentials";
    public IReadOnlyList<string> RequiredConfigFields => ["token_endpoint", "client_id", "client_auth_method"];
    public IReadOnlyList<string> RequiredSecretFields => ["client_secret"];
    public IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config) => ["Authorization"];

    public async Task ApplyAsync(
        IDictionary<string, string> headers,
        JsonElement config,
        IReadOnlyDictionary<string, string> secretRefs,
        IReadOnlyDictionary<string, string> secrets,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (httpClient is null)
            throw Terminal("OAuth token acquisition is unavailable in this process.");

        string endpointText = RequiredString(config, "token_endpoint");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw Terminal("OAuth token endpoint must be an absolute HTTPS URL without user information or a fragment.");
        }

        string clientId = RequiredString(config, "client_id");
        string method = RequiredString(config, "client_auth_method");
        if (method is not ("client_secret_basic" or "client_secret_post"))
            throw Terminal("OAuth client authentication method is invalid.");

        if (!secretRefs.TryGetValue("client_secret", out string? secretRef)
            || !secrets.TryGetValue("client_secret", out string? clientSecret))
        {
            throw Terminal("OAuth client secret could not be resolved.");
        }

        string? scope = NormalizeScope(config);
        var key = new TokenCacheKey(tenantId, endpoint, clientId, method, scope, secretRef);
        string accessToken = await GetTokenAsync(key, clientSecret, cancellationToken);
        headers.Add("Authorization", $"Bearer {accessToken}");
    }

    private async Task<string> GetTokenAsync(
        TokenCacheKey key,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var candidate = new Lazy<Task<CachedToken>>(
                () => AcquireAsync(key, clientSecret, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<Task<CachedToken>> acquisition = _tokens.GetOrAdd(key, candidate);
            bool completedBeforeThisCall = acquisition.IsValueCreated && acquisition.Value.IsCompletedSuccessfully;

            try
            {
                CachedToken token = await acquisition.Value;
                DateTimeOffset now = timeProvider.GetUtcNow();
                if (completedBeforeThisCall && token.ReusableUntil <= now)
                {
                    _tokens.TryRemove(new KeyValuePair<TokenCacheKey, Lazy<Task<CachedToken>>>(key, acquisition));
                    continue;
                }

                if (token.ReusableUntil <= now)
                    _tokens.TryRemove(new KeyValuePair<TokenCacheKey, Lazy<Task<CachedToken>>>(key, acquisition));

                return token.AccessToken;
            }
            catch
            {
                _tokens.TryRemove(new KeyValuePair<TokenCacheKey, Lazy<Task<CachedToken>>>(key, acquisition));
                throw;
            }
        }
    }

    private async Task<CachedToken> AcquireAsync(
        TokenCacheKey key,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, key.TokenEndpoint);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials")
        };
        if (key.Scope is not null)
            fields.Add(new("scope", key.Scope));

        if (key.Method == "client_secret_basic")
        {
            string credentials = $"{FormEncode(key.ClientId)}:{FormEncode(clientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }
        else
        {
            fields.Add(new("client_id", key.ClientId));
            fields.Add(new("client_secret", clientSecret));
        }

        request.Content = new FormUrlEncodedContent(fields);

        HttpResponseMessage response;
        try
        {
            response = await httpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DestinationAuthenticationException("OAuth token request timed out.", isTimeout: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new DestinationAuthenticationException("OAuth token endpoint could not be reached.");
        }

        using (response)
        {
            int statusCode = (int)response.StatusCode;
            (byte[] body, bool exceededLimit) bodyResult;
            try
            {
                bodyResult = await ReadBoundedAsync(response, cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new DestinationAuthenticationException("OAuth token response could not be read.", statusCode);
            }
            catch (IOException)
            {
                throw new DestinationAuthenticationException("OAuth token response could not be read.", statusCode);
            }

            (byte[] body, bool exceededLimit) = bodyResult;
            if (!response.IsSuccessStatusCode)
            {
                string? error = exceededLimit ? null : ReadOAuthError(body);
                bool transient = statusCode is 408 or 429 or >= 500
                    || error is "server_error" or "temporarily_unavailable";
                throw new DestinationAuthenticationException(
                    transient ? "OAuth token endpoint returned a transient failure." : "OAuth token request was rejected.",
                    statusCode,
                    isTerminal: !transient,
                    retryAfter: transient ? RetryAfterParser.Parse(response, timeProvider.GetUtcNow()) : null);
            }

            if (exceededLimit)
                throw Terminal("OAuth token response exceeded the allowed size.");

            return ParseSuccessfulToken(body);
        }
    }

    private CachedToken ParseSuccessfulToken(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string? accessToken = root.GetProperty("access_token").GetString();
            string? tokenType = root.GetProperty("token_type").GetString();
            double expiresIn = root.GetProperty("expires_in").GetDouble();
            if (string.IsNullOrWhiteSpace(accessToken)
                || !string.Equals(tokenType, "bearer", StringComparison.OrdinalIgnoreCase)
                || !double.IsFinite(expiresIn)
                || expiresIn <= 0
                || expiresIn > TimeSpan.MaxValue.TotalSeconds)
            {
                throw new JsonException();
            }

            SecretValueValidator.EnsureHeaderSafe(accessToken, "access_token");
            TimeSpan lifetime = TimeSpan.FromSeconds(expiresIn);
            TimeSpan refresh = TimeSpan.FromTicks(Math.Min(lifetime.Ticks / 10, TimeSpan.FromSeconds(30).Ticks));
            return new CachedToken(accessToken, timeProvider.GetUtcNow() + lifetime - refresh);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or ArgumentOutOfRangeException or DeliveryConfigurationException)
        {
            throw Terminal("OAuth token endpoint returned an invalid success response.");
        }
    }

    private static async Task<(byte[] Body, bool ExceededLimit)> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while (buffer.Length <= ResponseMaxBytes && (read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            buffer.Write(chunk, 0, read);

        byte[] body = buffer.ToArray();
        return body.Length > ResponseMaxBytes
            ? (body[..ResponseMaxBytes], true)
            : (body, false);
    }

    private static string? ReadOAuthError(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out JsonElement error)
                ? error.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string RequiredString(JsonElement config, string name)
    {
        if (!config.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Terminal($"OAuth configuration field '{name}' is required.");
        }

        return value.GetString()!;
    }

    private static string? NormalizeScope(JsonElement config)
    {
        if (!config.TryGetProperty("scope", out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw Terminal("OAuth scope must be a string.");

        string normalized = string.Join(' ', value.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized;
    }

    private static string FormEncode(string value) => WebUtility.UrlEncode(value);

    private static DestinationAuthenticationException Terminal(string message) => new(message, isTerminal: true);

    private sealed record TokenCacheKey(
        Guid TenantId,
        Uri TokenEndpoint,
        string ClientId,
        string Method,
        string? Scope,
        string SecretRef);

    private sealed record CachedToken(string AccessToken, DateTimeOffset ReusableUntil);
}
