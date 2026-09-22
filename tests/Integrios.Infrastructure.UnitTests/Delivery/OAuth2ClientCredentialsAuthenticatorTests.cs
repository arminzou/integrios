using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Infrastructure.UnitTests;

public sealed class OAuth2ClientCredentialsAuthenticatorTests
{
    private static readonly Guid TenantId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task BasicMethod_CoalescesConcurrentMisses_AndDoesNotDuplicateCredentials()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? authorization = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = await request.Content!.ReadAsStringAsync();
            started.TrySetResult();
            await release.Task;
            return Token("shared-token", 3600);
        });
        var authenticator = Create(handler);
        JsonElement config = Config("client_secret_basic", "read write");

        Task<Dictionary<string, string>> first = ApplyAsync(authenticator, config);
        await started.Task;
        Task<Dictionary<string, string>> second = ApplyAsync(authenticator, config);
        release.SetResult();
        Dictionary<string, string>[] headers = await Task.WhenAll(first, second);

        handler.Calls.ShouldBe(1);
        string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("client-id:client-secret"));
        authorization.ShouldBe($"Basic {expected}");
        body.ShouldBe("grant_type=client_credentials&scope=read+write");
        headers.Select(item => item["Authorization"]).ShouldAllBe(value => value == "Bearer shared-token");
    }

    [Fact]
    public async Task PostMethod_SendsCredentialsOnlyInFormBody()
    {
        string? authorization = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = await request.Content!.ReadAsStringAsync();
            return Token("post-token", 3600);
        });
        var authenticator = Create(handler);

        Dictionary<string, string> headers = await ApplyAsync(authenticator, Config("client_secret_post"));

        authorization.ShouldBeNull();
        body.ShouldBe("grant_type=client_credentials&client_id=client-id&client_secret=client-secret");
        headers["Authorization"].ShouldBe("Bearer post-token");
    }

    [Fact]
    public async Task Failure_IsNotCached_AndTemporaryOAuthErrorHonorsBoundedRetryAfter()
    {
        int responseNumber = 0;
        var handler = new StubHandler(_ => Task.FromResult(handlerResponse()));
        HttpResponseMessage handlerResponse()
        {
            responseNumber++;
            if (responseNumber == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"temporarily_unavailable","detail":"client-secret"}""")
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return response;
            }

            return Token("recovered-token", 3600);
        }

        var authenticator = Create(handler);
        DestinationAuthenticationException error = await Should.ThrowAsync<DestinationAuthenticationException>(
            () => ApplyAsync(authenticator, Config("client_secret_post")));

        error.IsTerminal.ShouldBeFalse();
        error.StatusCode.ShouldBe(400);
        error.RetryAfter.ShouldBe(TimeSpan.FromMinutes(15));
        error.Message.ShouldNotContain("client-secret");

        Dictionary<string, string> headers = await ApplyAsync(authenticator, Config("client_secret_post"));
        handler.Calls.ShouldBe(2);
        headers["Authorization"].ShouldBe("Bearer recovered-token");
    }

    [Fact]
    public async Task CacheKey_SeparatesTenantsAndAcquisitionContracts()
    {
        var handler = new StubHandler(_ => Task.FromResult(Token($"token-{Guid.NewGuid()}", 3600)));
        var authenticator = Create(handler);

        await ApplyAsync(authenticator, Config("client_secret_post"), TenantId);
        await ApplyAsync(authenticator, Config("client_secret_post"), TenantId);
        await ApplyAsync(authenticator, Config("client_secret_post", "other"), TenantId);
        await ApplyAsync(authenticator, Config("client_secret_post"), Guid.NewGuid());

        handler.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Cache_RefreshesAtTenPercentEarlyBoundary()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(_ => Task.FromResult(Token($"token-{Guid.NewGuid()}", 100)));
        var authenticator = Create(handler, clock);

        await ApplyAsync(authenticator, Config("client_secret_post"));
        clock.Advance(TimeSpan.FromSeconds(89));
        await ApplyAsync(authenticator, Config("client_secret_post"));
        clock.Advance(TimeSpan.FromSeconds(2));
        await ApplyAsync(authenticator, Config("client_secret_post"));

        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task OversizedServerFailure_RemainsTransientWithoutBufferingTheBody()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new ByteArrayContent(new byte[OAuth2ClientCredentialsAuthenticator.ResponseMaxBytes + 1])
        };
        var authenticator = Create(new StubHandler(_ => Task.FromResult(response)));

        DestinationAuthenticationException error = await Should.ThrowAsync<DestinationAuthenticationException>(
            () => ApplyAsync(authenticator, Config("client_secret_post")));

        error.IsTerminal.ShouldBeFalse();
        error.StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task StalledResponseBody_TimesOutWithinTheHttpClientTimeout()
    {
        var pipe = new Pipe();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(pipe.Reader.AsStream())
        };
        var authenticator = Create(
            new StubHandler(_ => Task.FromResult(response)),
            timeout: TimeSpan.FromMilliseconds(50));
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        DestinationAuthenticationException error = await Should.ThrowAsync<DestinationAuthenticationException>(
            () => ApplyAsync(authenticator, Config("client_secret_post"), cancellationToken: testTimeout.Token));

        error.IsTimeout.ShouldBeTrue();
        error.IsTerminal.ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://identity.example/token")]
    [InlineData("https://user@identity.example/token")]
    [InlineData("https://identity.example/token#fragment")]
    public async Task InvalidEndpoint_IsTerminalWithoutSendingRequest(string endpoint)
    {
        var handler = new StubHandler(_ => Task.FromResult(Token("unused", 3600)));
        var authenticator = Create(handler);

        DestinationAuthenticationException error = await Should.ThrowAsync<DestinationAuthenticationException>(
            () => ApplyAsync(authenticator, Config("client_secret_post", endpoint: endpoint)));

        error.IsTerminal.ShouldBeTrue();
        handler.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"secret-token\",\"token_type\":\"mac\",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"secret-token\",\"token_type\":\"bearer\",\"expires_in\":0}")]
    public async Task MalformedSuccess_IsTerminalAndDoesNotLeakResponse(string json)
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        }));
        var authenticator = Create(handler);

        DestinationAuthenticationException error = await Should.ThrowAsync<DestinationAuthenticationException>(
            () => ApplyAsync(authenticator, Config("client_secret_post")));

        error.IsTerminal.ShouldBeTrue();
        error.Message.ShouldBe("OAuth token endpoint returned an invalid success response.");
        error.Message.ShouldNotContain("secret-token");
    }

    private static OAuth2ClientCredentialsAuthenticator Create(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(100) }, timeProvider ?? TimeProvider.System);

    private static async Task<Dictionary<string, string>> ApplyAsync(
        OAuth2ClientCredentialsAuthenticator authenticator,
        JsonElement config,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await authenticator.ApplyAsync(
            headers,
            config,
            new Dictionary<string, string> { ["client_secret"] = "oauth-client-secret" },
            new Dictionary<string, string> { ["client_secret"] = "client-secret" },
            tenantId ?? TenantId,
            cancellationToken);
        return headers;
    }

    private static JsonElement Config(
        string method,
        string? scope = null,
        string endpoint = "https://identity.example/token")
    {
        var values = new Dictionary<string, string>
        {
            ["token_endpoint"] = endpoint,
            ["client_id"] = "client-id",
            ["client_auth_method"] = method
        };
        if (scope is not null)
            values["scope"] = scope;

        return JsonSerializer.SerializeToElement(values);
    }

    private static HttpResponseMessage Token(string accessToken, int expiresIn) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = expiresIn
        }))
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return response(request);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
