using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Application.Events;
using Integrios.Domain.Common;
using Integrios.Domain.Events;
using Integrios.Domain.Tenants;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Ingress.Tests.Auth;

public sealed class ApiKeyAuthHandlerTests(ApiTestAppFixture fixture)
    : IClassFixture<ApiTestAppFixture>, IAsyncLifetime
{
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        fixture.Reset();
        client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    // Header parsing: malformed or missing -> 401

    [Theory]
    [InlineData(null)]                  // no header
    [InlineData("Bearer token")]        // wrong scheme
    [InlineData("ApiKey nocolon")]      // no colon separator
    [InlineData("ApiKey :secret")]      // empty key_id
    [InlineData("ApiKey keyid:")]       // empty secret
    public async Task BadHeader_Returns401(string? authHeader)
    {
        var response = await PostEventsAsync(authHeader);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Repository filtering: unknown key -> 401

    [Fact]
    public async Task UnknownPublicKey_Returns401()
    {
        var response = await PostEventsAsync("ApiKey unknown:secret");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Repository filtering: expired/inactive credentials returned as null by repo -> 401
    // (The repository enforces this via the SQL WHERE clause; the filter sees null.)

    [Fact]
    public async Task InactiveOrExpiredCredential_Returns401()
    {
        // Repository returns null, simulating active/expiry filtering in SQL.
        var response = await PostEventsAsync("ApiKey key_test:any-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Hash verification: valid key_id but wrong secret -> 401

    [Fact]
    public async Task WrongSecret_Returns401()
    {
        var (apiKey, tenant) = BuildValidApiKey("correct-secret");
        fixture.ApiKeyRepository.Result = (apiKey, tenant);

        var response = await PostEventsAsync($"ApiKey {apiKey.PublicKey}:wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Happy path: valid credential passes the filter

    [Fact]
    public async Task ValidCredential_PassesFilter()
    {
        const string secret = "correct-secret";
        var (apiKey, tenant) = BuildValidApiKey(secret);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);

        var response = await PostEventsAsync($"ApiKey {apiKey.PublicKey}:{secret}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // 401 response carries WWW-Authenticate header

    [Fact]
    public async Task Rejected_Response_HasWwwAuthenticateHeader()
    {
        var response = await PostEventsAsync(authHeader: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("WWW-Authenticate"));
    }

    [Fact]
    public async Task GetEvent_MissingAuth_Returns401()
    {
        var response = await GetEventAsync(Guid.NewGuid(), authHeader: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetEvent_ValidAuthAndUnknownEvent_Returns404()
    {
        const string secret = "correct-secret";
        var (apiKey, tenant) = BuildValidApiKey(secret);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);
        fixture.EventRepository.GetEventResult = null;

        var response = await GetEventAsync(Guid.NewGuid(), $"ApiKey {apiKey.PublicKey}:{secret}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetEvent_ValidAuthAndKnownEvent_Returns200()
    {
        const string secret = "correct-secret";
        var (apiKey, tenant) = BuildValidApiKey(secret);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);

        var eventId = Guid.NewGuid();
        var expected = new GetEventResponse
        {
            EventId = eventId,
            Status = EventStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
            ProcessedAt = null,
            FailedAt = null
        };
        fixture.EventRepository.GetEventResult = expected;

        var response = await GetEventAsync(eventId, $"ApiKey {apiKey.PublicKey}:{secret}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<GetEventResponse>();
        Assert.NotNull(body);
        Assert.Equal(expected.EventId, body.EventId);
        Assert.Equal(expected.Status, body.Status);
        Assert.Equal(expected.AcceptedAt, body.AcceptedAt);
        Assert.Equal(expected.ProcessedAt, body.ProcessedAt);
        Assert.Equal(expected.FailedAt, body.FailedAt);
    }

    // Helpers

    private Task<HttpResponseMessage> PostEventsAsync(string? authHeader)
    {
        var request = new IngestEventRequest
        {
            SourceEventId = "evt_test_1",
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"paymentId":"pay_1","amount":1200}""").RootElement.Clone(),
            Metadata = JsonDocument.Parse("""{"source":"tests"}""").RootElement.Clone(),
            IdempotencyKey = "idem-test"
        };

        var message = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(request)
        };
        if (authHeader is not null)
        {
            message.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }

        return client.SendAsync(message);
    }

    private Task<HttpResponseMessage> GetEventAsync(Guid eventId, string? authHeader)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, $"/events/{eventId}");
        if (authHeader is not null)
        {
            message.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }

        return client.SendAsync(message);
    }

    public static (ApiKey ApiKey, Tenant Tenant) BuildValidApiKeyPublic(string secret) => BuildValidApiKey(secret);

    private static (ApiKey ApiKey, Tenant Tenant) BuildValidApiKey(string secret)
    {
        var hash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

        var tenantId = Guid.NewGuid();
        return (
            new ApiKey
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "test-key",
                PublicKey = "key_test",
                SecretHash = hash,
                Scopes = ["events.write"],
                Status = OperationalStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new Tenant
            {
                Id = tenantId,
                Slug = "test-tenant",
                Name = "Test Tenant",
                Status = OperationalStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
    }
}

public sealed class ApiTestAppFixture : IDisposable
{
    public StubApiKeyRepository ApiKeyRepository { get; } = new();
    public StubEventRepository EventRepository { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    public ApiTestAppFixture()
    {
        Factory = new CustomApiFactory(ApiKeyRepository, EventRepository);
    }

    public void Reset()
    {
        ApiKeyRepository.Result = null;
        EventRepository.GetEventResult = null;
        EventRepository.ReplayResult = false;
    }

    public void Dispose()
    {
        Factory.Dispose();
    }
}

internal sealed class CustomApiFactory(
    StubApiKeyRepository apiKeyRepository,
    StubEventRepository eventRepository) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=test;Username=test;Password=test"
            }));

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IApiKeyRepository>(apiKeyRepository);
            services.AddSingleton<IEventRepository>(eventRepository);
        });
    }
}

public sealed class StubApiKeyRepository : IApiKeyRepository
{
    public (ApiKey ApiKey, Tenant Tenant)? Result { get; set; }

    public Task<(ApiKey ApiKey, Tenant Tenant)?> FindActiveByPublicKeyAsync(
        string publicKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result);

    public Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<(IReadOnlyList<ApiKey> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

public sealed class StubEventRepository : IEventRepository
{
    public GetEventResponse? GetEventResult { get; set; }
    public bool ReplayResult { get; set; } = false;

    public Task<IngestEventResponse> IngestAsync(
        Guid tenantId,
        IngestEventRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new IngestEventResponse
        {
            EventId = Guid.NewGuid(),
            Status = EventStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
            IsDuplicate = false
        });
    }

    public Task<GetEventResponse?> GetEventByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetEventResult);
    }

    public Task<bool> ReplayEventAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ReplayResult);
    }
}
