using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Ingestion.UnitTests;

// Exercises the real webhook command, and the
// configuration-backed ISourceVerificationSecretResolver end to end through the HTTP endpoint;
// only the Postgres-backed ISourceEndpointResolver and IEventAcceptance ports are stubbed. This is
// the "production path" i7a.5 requires host isolation to be proven against.
public sealed class WebhookEndpointTests(ApiTestAppFixture fixture)
    : IClassFixture<ApiTestAppFixture>, IAsyncLifetime
{
    private const string SignatureHeaderName = "X-Signature";
    private const string EventTypeHeaderName = "X-Event-Type";
    private const string DeliveryIdHeaderName = "X-Delivery-Id";

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
    public async Task PostWebhook_ValidSignature_AcceptsAndDerivesEventTypeFromContext()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        HttpResponseMessage response = await SendAsync(
            callbackId, """{"action":"opened","number":1}""", "issue.opened", "delivery-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fixture.EventAcceptance.LastSubmission.ShouldNotBeNull();
        EventSubmission submission = fixture.EventAcceptance.LastSubmission;
        submission.EventType.ShouldBe("test.issue.opened");
        submission.SourceEventId.ShouldBe("delivery-1");
        submission.IdempotencyKey.ShouldBe($"{fixture.SourceEndpointResolver.Result!.SourceId}:delivery-1");
        submission.Payload.ValueKind.ShouldBe(JsonValueKind.Object);
        submission.Payload.GetProperty("number").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task PostWebhook_DefaultHmacShape_VerifiesWithNoConfigOverride()
    {
        // No header_name/prefix/encoding in Config: the platform default (X-Hub-Signature-256,
        // "sha256=" prefix, hex) applies, per ConnectorManifestParser.ValidatePlatformSchemes
        // requiring hmac_sha256 to declare no required config.
        Guid callbackId = Guid.NewGuid();
        ResolvedSourceEndpoint endpoint = BuildResolvedEndpoint() with
        {
            SourceVerification = new SourceVerification
            {
                Scheme = "hmac_sha256",
                Config = JsonSerializer.Deserialize<JsonElement>("{}"),
                SecretRefs = JsonSerializer.Deserialize<JsonElement>(
                    $$"""{"secret":"{{ApiTestAppFixture.WebhookSecretReference}}"}"""),
            },
        };
        fixture.SourceEndpointResolver.Result = endpoint;

        string body = """{"action":"opened"}""";
        string signature = "sha256=" + SignBody(body);
        HttpRequestMessage request = new(HttpMethod.Post, $"/webhooks/{callbackId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        request.Headers.TryAddWithoutValidation(EventTypeHeaderName, "issue.opened");
        request.Headers.TryAddWithoutValidation(DeliveryIdHeaderName, "delivery-default");

        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task PostWebhook_NoVerificationConfigured_SkipsVerificationAndAccepts()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with { SourceVerification = null };

        HttpRequestMessage request = new(HttpMethod.Post, $"/webhooks/{callbackId}")
        {
            Content = new StringContent("""{"action":"opened"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(EventTypeHeaderName, "issue.opened");
        request.Headers.TryAddWithoutValidation(DeliveryIdHeaderName, "delivery-open");

        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task PostWebhook_InvalidSignature_Returns401()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        HttpRequestMessage request = BuildRequest(
            callbackId, """{"action":"opened"}""", new string('0', 64), "issue.opened", "delivery-2");

        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fixture.EventAcceptance.LastSubmission.ShouldBeNull();
    }

    [Fact]
    public async Task PostWebhook_UnknownCallback_Returns404()
    {
        fixture.SourceEndpointResolver.Result = null;

        HttpResponseMessage response = await SendAsync(Guid.NewGuid(), """{"a":1}""", "push", "delivery-3");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostWebhook_NonObjectPayload_Returns400()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        HttpResponseMessage response = await SendAsync(callbackId, "[1,2,3]", "push", "delivery-4");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostWebhook_BodyExceedsBound_Returns413()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint();

        string oversizedBody = "{\"padding\":\"" + new string('a', 2_000_000) + "\"}";
        HttpRequestMessage request = BuildRequest(callbackId, oversizedBody, "deadbeef", "push", "delivery-5");

        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Guid callbackId, string body, string eventTypeValue, string deliveryId)
    {
        string signature = SignBody(body);
        HttpRequestMessage request = BuildRequest(callbackId, body, signature, eventTypeValue, deliveryId);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage BuildRequest(
        Guid callbackId,
        string body,
        string signatureHeaderValue,
        string eventTypeValue,
        string deliveryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{callbackId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(SignatureHeaderName, signatureHeaderValue);
        request.Headers.TryAddWithoutValidation(EventTypeHeaderName, eventTypeValue);
        request.Headers.TryAddWithoutValidation(DeliveryIdHeaderName, deliveryId);
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
        SourceId = Guid.NewGuid(),
        ConnectionId = Guid.NewGuid(),
        ConnectorKey = "test_webhook",
        SourceVerification = new SourceVerification
        {
            Scheme = "hmac_sha256",
            // Overrides the platform default (X-Hub-Signature-256 / "sha256=" prefix / hex) via
            // optional Config keys, proving the shape stays per-Connector-overridable.
            Config = JsonSerializer.Deserialize<JsonElement>($$"""{"header_name":"{{SignatureHeaderName}}","encoding":"hex","prefix":""}"""),
            SecretRefs = JsonSerializer.Deserialize<JsonElement>(
                $$"""{"secret":"{{ApiTestAppFixture.WebhookSecretReference}}"}"""),
        },
        SourceContractSchema = null,
        // $context.headers keys are lower-cased by BuildContext regardless of how the request sent
        // them, so the mapping looks them up in lower case too.
        SourceMapping = new TransformSpec(
            "jsonata",
            "1",
            $$"""{ "event_type": "test." & $context.headers.`x-event-type`, "source_event_id": $context.headers.`x-delivery-id`, "payload": $ }"""),
    };
}
