using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Api.Infrastructure.Data.Events;
using Integrios.Api.Infrastructure.Data.Tenants;
using Integrios.Core.Contracts;
using Integrios.Core.Domain.Common;
using Integrios.Core.Domain.Events;
using Integrios.Core.Domain.Tenants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Api.Tests.Auth;

public sealed class ApiKeyEndpointFilterTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Header parsing: malformed or missing → 401

    [Theory]
    [InlineData(null)]                  // no header
    [InlineData("Bearer token")]        // wrong scheme
    [InlineData("ApiKey nocolon")]      // no colon separator
    [InlineData("ApiKey :secret")]      // empty key_id
    [InlineData("ApiKey keyid:")]       // empty secret
    public async Task BadHeader_Returns401(string? authHeader)
    {
        var client = BuildClient(repositoryResult: null, authHeader);
        var response = await PostEventsAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Repository filtering: unknown key → 401

    [Fact]
    public async Task UnknownKeyId_Returns401()
    {
        var client = BuildClient(repositoryResult: null, "ApiKey unknown:secret");
        var response = await PostEventsAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Repository filtering: expired/inactive credentials returned as null by repo → 401
    // (The repository enforces this via the SQL WHERE clause; the filter sees null.)

    [Fact]
    public async Task InactiveOrExpiredCredential_Returns401()
    {
        // Repository returns null, simulating active/expiry filtering in SQL.
        var client = BuildClient(repositoryResult: null, "ApiKey key_test:any-secret");
        var response = await PostEventsAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Hash verification: valid key_id but wrong secret → 401

    [Fact]
    public async Task WrongSecret_Returns401()
    {
        var (credential, tenant) = BuildValidCredential("correct-secret");
        var client = BuildClient((credential, tenant), $"ApiKey {credential.KeyId}:wrong-secret");
        var response = await PostEventsAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Happy path: valid credential passes the filter

    [Fact]
    public async Task ValidCredential_PassesFilter()
    {
        const string secret = "correct-secret";
        var (credential, tenant) = BuildValidCredential(secret);
        var client = BuildClient((credential, tenant), $"ApiKey {credential.KeyId}:{secret}");
        var response = await PostEventsAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // 401 response carries WWW-Authenticate header

    [Fact]
    public async Task Rejected_Response_HasWwwAuthenticateHeader()
    {
        var client = BuildClient(repositoryResult: null, null);
        var response = await PostEventsAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("WWW-Authenticate"));
    }

    // Helpers

    private HttpClient BuildClient(
        (ApiCredential Credential, Tenant Tenant)? repositoryResult,
        string? authHeader)
    {
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] =
                        "Host=localhost;Database=test;Username=test;Password=test"
                }));

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IApiCredentialRepository>(
                    new StubCredentialRepository(repositoryResult));
                services.AddSingleton<IEventIngestionRepository>(new StubEventIngestionRepository());
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        if (authHeader is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);

        return client;
    }

    private static Task<HttpResponseMessage> PostEventsAsync(HttpClient client)
    {
        var request = new IngestEventRequest
        {
            SourceEventId = "evt_test_1",
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"paymentId":"pay_1","amount":1200}""").RootElement.Clone(),
            Metadata = JsonDocument.Parse("""{"source":"tests"}""").RootElement.Clone(),
            IdempotencyKey = "idem-test"
        };

        return client.PostAsJsonAsync("/events", request);
    }

    private static (ApiCredential Credential, Tenant Tenant) BuildValidCredential(string secret)
    {
        var hash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

        var tenantId = Guid.NewGuid();
        return (
            new ApiCredential
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "test-key",
                KeyId = "key_test",
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

    private sealed class StubCredentialRepository(
        (ApiCredential Credential, Tenant Tenant)? result) : IApiCredentialRepository
    {
        public Task<(ApiCredential Credential, Tenant Tenant)?> FindActiveByKeyIdAsync(
            string keyId, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubEventIngestionRepository : IEventIngestionRepository
    {
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
    }
}
