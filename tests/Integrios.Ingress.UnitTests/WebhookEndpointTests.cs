using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Domain.Connections;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Ingress.UnitTests;

// Exercises the real VerifiedWebhookIngressAdapter, IngressSourceAdapterRuntime, and the
// configuration-backed ISourceVerificationSecretResolver end to end through the HTTP endpoint;
// only the Postgres-backed ISourceEndpointResolver and IEventAcceptance ports are stubbed. This is
// the "production path" i7a.5 requires host isolation to be proven against.
public sealed class WebhookEndpointTests(ApiTestAppFixture fixture)
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

    [Fact]
    public async Task PostWebhook_ValidSignature_AcceptsAndDerivesActionQualifiedEventType()
    {
        Guid endpointId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        HttpResponseMessage response = await SendAsync(
            endpointId, """{"action":"opened","number":1}""", "X-GitHub-Event", "issues", "delivery-1");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(fixture.EventAcceptance.LastSubmission);
        EventSubmission submission = fixture.EventAcceptance.LastSubmission;
        Assert.Equal("github.issues.opened", submission.EventType);
        Assert.Equal("delivery-1", submission.SourceEventId);
        Assert.Equal($"{endpointId}:delivery-1", submission.IdempotencyKey);
        Assert.Equal(JsonValueKind.Object, submission.Payload.ValueKind);
        Assert.Equal(1, submission.Payload.GetProperty("number").GetInt32());
    }

    // Resolves the design's deferred "verified GitHub ping" Known Unknown: a verified ping becomes
    // an ordinary Event under its derived type, with no adapter-level special-casing.
    [Fact]
    public async Task PostWebhook_GitHubPing_AcceptsAsOrdinaryPingEventWithNoActionSegment()
    {
        Guid endpointId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        HttpResponseMessage response = await SendAsync(
            endpointId, """{"zen":"hello"}""", "X-GitHub-Event", "ping", "delivery-ping");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("github.ping", fixture.EventAcceptance.LastSubmission!.EventType);
    }

    [Fact]
    public async Task PostWebhook_InvalidSignature_Returns401()
    {
        Guid endpointId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        HttpRequestMessage request = BuildRequest(
            endpointId, """{"action":"opened"}""", "sha256=" + new string('0', 64),
            "X-GitHub-Event", "issues", "delivery-2");

        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(fixture.EventAcceptance.LastSubmission);
    }

    [Fact]
    public async Task PostWebhook_UnknownEndpoint_Returns404()
    {
        fixture.SourceEndpointResolver.Result = null;

        HttpResponseMessage response = await SendAsync(
            Guid.NewGuid(), """{"a":1}""", "X-GitHub-Event", "push", "delivery-3");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_NonObjectPayload_Returns400()
    {
        Guid endpointId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        HttpResponseMessage response = await SendAsync(endpointId, "[1,2,3]", "X-GitHub-Event", "push", "delivery-4");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_BodyExceedsBound_Returns413()
    {
        Guid endpointId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        string oversizedBody = "{\"padding\":\"" + new string('a', 2_000_000) + "\"}";
        HttpRequestMessage request = BuildRequest(
            endpointId, oversizedBody, "sha256=deadbeef", "X-GitHub-Event", "push", "delivery-5");

        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Guid endpointId, string body, string eventTypeHeaderName, string eventTypeValue, string deliveryId)
    {
        string signature = "sha256=" + SignBody(body);
        HttpRequestMessage request = BuildRequest(endpointId, body, signature, eventTypeHeaderName, eventTypeValue, deliveryId);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage BuildRequest(
        Guid endpointId,
        string body,
        string signatureHeaderValue,
        string eventTypeHeaderName,
        string eventTypeValue,
        string deliveryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/github/{endpointId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signatureHeaderValue);
        request.Headers.TryAddWithoutValidation(eventTypeHeaderName, eventTypeValue);
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", deliveryId);
        return request;
    }

    private static string SignBody(string body)
    {
        byte[] key = Encoding.UTF8.GetBytes(ApiTestAppFixture.WebhookSecretValue);
        byte[] hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ResolvedSourceEndpoint BuildResolvedEndpoint() => new()
    {
        TenantId = Guid.NewGuid(),
        TenantSlug = ApiTestAppFixture.WebhookTenantSlug,
        TopicId = Guid.NewGuid(),
        ConnectionId = Guid.NewGuid(),
        IntegrationKey = "github",
        SourceAdapterKey = "verified_webhook",
        SourceAdapterContractVersion = 1,
        SourceAdapterConfig = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "signature_header": "X-Hub-Signature-256",
              "signature_encoding": "hex",
              "signature_prefix": "sha256=",
              "delivery_id_header": "X-GitHub-Delivery",
              "event_type_header": "X-GitHub-Event",
              "event_type_action_field": "action"
            }
            """),
        SourceVerification = new ConnectionSchemeSelection
        {
            Scheme = "hmac_sha256",
            Config = JsonSerializer.Deserialize<JsonElement>("{}"),
            SecretRefs = JsonSerializer.Deserialize<JsonElement>(
                $$"""{"secret":"{{ApiTestAppFixture.WebhookSecretReference}}"}"""),
        },
    };
}
