using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Ingestion.UnitTests;

public sealed class TenantApiKeyAuthHandlerTests(IngestionApiFixture fixture)
    : IClassFixture<IngestionApiFixture>, IAsyncLifetime
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

    public const string TestToken = "intg_deadbeefcafebabe00112233445566778899aabbccddeeff00112233445566";
    private const string WrongToken = "intg_deaddeaddeaddeaddeaddeaddeaddeaddeaddeaddeaddeaddeaddeaddeadde";

    // Header parsing: malformed or missing -> 401

    [Theory]
    [InlineData(null)]                              // no header
    [InlineData("Bearer intg_abc123")]              // wrong scheme
    [InlineData("TenantApiKey ")]                         // empty value
    [InlineData("TenantApiKey wrong_prefix_secret")]      // doesn't start with intg_
    [InlineData("TenantApiKey intg_")]                    // nothing after intg_
    public async Task BadHeader_Returns401(string? authHeader)
    {
        HttpResponseMessage response = await PostEventsAsync(authHeader);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Repository filtering: unknown key -> 401

    [Fact]
    public async Task UnknownKeyId_Returns401()
    {
        HttpResponseMessage response = await PostEventsAsync($"TenantApiKey {TestToken}");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Hash verification: valid keyId but wrong token -> 401

    [Fact]
    public async Task WrongToken_Returns401()
    {
        (TenantApiKey tenantApiKey, Tenant tenant) = BuildValidTenantApiKey(TestToken);
        fixture.TenantApiKeyRepository.Result = (tenantApiKey, tenant);

        HttpResponseMessage response = await PostEventsAsync($"TenantApiKey {WrongToken}");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Happy path: valid credential passes the filter

    [Fact]
    public async Task ValidCredential_AllowsRequestThrough()
    {
        (TenantApiKey tenantApiKey, Tenant tenant) = BuildValidTenantApiKey(TestToken);
        fixture.TenantApiKeyRepository.Result = (tenantApiKey, tenant);

        HttpResponseMessage response = await PostEventsAsync($"TenantApiKey {TestToken}");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // 401 response carries WWW-Authenticate header

    [Fact]
    public async Task Rejected_Response_HasWwwAuthenticateHeader()
    {
        HttpResponseMessage response = await PostEventsAsync(authHeader: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Contains("WWW-Authenticate").ShouldBeTrue();
    }

    [Fact]
    public async Task GetEvent_MissingAuth_Returns401()
    {
        HttpResponseMessage response = await GetEventAsync(Guid.NewGuid(), authHeader: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Helpers

    private Task<HttpResponseMessage> PostEventsAsync(string? authHeader)
    {
        var request = new
        {
            source_event_id = "evt_test_1",
            event_type = "payment.created",
            payload = JsonDocument.Parse("""{"paymentId":"pay_1","amount":1200}""").RootElement.Clone(),
            metadata = JsonDocument.Parse("""{"source":"tests"}""").RootElement.Clone(),
        };

        HttpRequestMessage message = new(HttpMethod.Post, $"/events?source_id={Guid.NewGuid()}")
        {
            Content = JsonContent.Create(request, options: HostJson.Options)
        };
        if (authHeader is not null)
            message.Headers.TryAddWithoutValidation("Authorization", authHeader);

        return client.SendAsync(message);
    }

    private Task<HttpResponseMessage> GetEventAsync(Guid eventId, string? authHeader)
    {
        HttpRequestMessage message = new(HttpMethod.Get, $"/events/{eventId}");
        if (authHeader is not null)
            message.Headers.TryAddWithoutValidation("Authorization", authHeader);

        return client.SendAsync(message);
    }

    public static (TenantApiKey TenantApiKey, Tenant Tenant) BuildValidTenantApiKeyPublic(string token) => BuildValidTenantApiKey(token);

    public static (TenantApiKey TenantApiKey, Tenant Tenant) BuildValidTenantApiKey(string token)
    {
        string hash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        Guid tenantId = Guid.NewGuid();
        return (
            new TenantApiKey
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "test-key",
                KeyPrefix = token[..12],
                KeyHash = hash,
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
