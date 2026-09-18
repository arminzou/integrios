using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Application.Telemetry;
using Integrios.Tests.Shared;
using Integrios.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Ingestion.UnitTests;

// Exercises the real webhook command, and the
// configuration-backed ISourceVerificationSecretResolver end to end through the HTTP endpoint;
// only the Postgres-backed ISourceEndpointResolver and IEventAcceptance ports are stubbed. This is
// the "production path" i7a.5 requires host isolation to be proven against.
public sealed class WebhookEndpointTests(IngestionApiFixture fixture)
    : IClassFixture<IngestionApiFixture>, IAsyncLifetime
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
        submission.IdempotencyKey!.Length.ShouldBe(97);
        submission.IdempotencyKey.ShouldNotContain("delivery-1", Case.Sensitive);
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
                    $$"""{"secret":"{{IngestionApiFixture.WebhookSecretReference}}"}"""),
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

    // A Source may name a secret that was never provisioned, and nothing at authoring time can tell:
    // the control plane holds names and the data plane holds values, deliberately. So the failure
    // lands here, per request. 5xx is the honest status — the request is fine and a retry succeeds
    // once the secret exists — and the reference stays out of the response, because an external
    // caller has no business learning how secrets are named.
    [Fact]
    public async Task PostWebhook_VerificationSecretDoesNotResolve_FailsWithoutNamingTheReference()
    {
        const string absentReference = "never_provisioned";
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with
        {
            SourceVerification = new SourceVerification
            {
                Scheme = "hmac_sha256",
                Config = JsonSerializer.Deserialize<JsonElement>("{}"),
                SecretRefs = JsonSerializer.Deserialize<JsonElement>($$"""{"secret":"{{absentReference}}"}"""),
            },
        };

        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        HttpResponseMessage response = await SendAsync(
            callbackId, """{"action":"opened"}""", "issue.opened", "delivery-1");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("The Source could not be verified.");
        body.ShouldNotContain(absentReference);
        body.ShouldNotContain("configuration");
        // Counted, because the refusal happens before the acceptance boundary and leaves no row.
        metrics.ForInstrument("integrios_ingest_secret_resolution_failures").ShouldNotBeEmpty();
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
    public async Task PostWebhook_JsonPointerIdentity_AcceptsTheBoundedBodyValue()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with
        {
            EventIdentityRule = new SourceEventIdentityRule { Kind = "json_path", Value = "/delivery/id" }
        };

        HttpResponseMessage response = await SendAsync(
            callbackId, """{"delivery":{"id":"body-delivery-1"},"action":"opened"}""", "issue.opened", "ignored");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fixture.EventAcceptance.LastSubmission!.SourceEventId.ShouldBe("body-delivery-1");
    }

    // A rule is read before the mapping and is fixed for the Source's life, so without this a
    // provider that omits its delivery header on a replay has every such request refused forever.
    [Fact]
    public async Task PostWebhook_MissingIdentity_IsRefusedWhenTheRuleDoesNotAllowIt()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with
        {
            EventIdentityRule = new SourceEventIdentityRule { Kind = "json_path", Value = "/delivery/id" }
        };

        HttpResponseMessage refused = await SendAsync(callbackId, """{"action":"opened"}""", "issue.opened", "ignored");

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // The rule yields nothing and the Source contract's mapping supplies the identity instead. This
    // is the composition the permission exists for: the immutable rule is the preferred identity, a
    // mapped one is the fallback, and only the mapped one may be absent.
    [Fact]
    public async Task PostWebhook_MissingIdentityTheRuleAllows_FallsBackToTheMappedIdentity()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with
        {
            EventIdentityRule = new SourceEventIdentityRule
            {
                Kind = "json_path",
                Value = "/delivery/id",
                AllowMissing = true
            }
        };

        HttpResponseMessage response = await SendAsync(
            callbackId, """{"action":"opened"}""", "issue.opened", "mapped-delivery");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fixture.EventAcceptance.LastSubmission!.SourceEventId.ShouldBe("mapped-delivery");
    }

    // Neither path produced one, which is a real state: the Event is accepted and carries no
    // identity, so nothing deduplicates it.
    [Fact]
    public async Task PostWebhook_MissingIdentityAndNoMappedIdentity_IsAcceptedWithoutOne()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with
        {
            EventIdentityRule = new SourceEventIdentityRule
            {
                Kind = "json_path",
                Value = "/delivery/id",
                AllowMissing = true
            },
            SourceMapping = new TransformSpec(
                "jsonata",
                "1",
                """{ "event_type": "test.fixed", "payload": $ }""")
        };

        HttpResponseMessage response = await SendAsync(
            callbackId, """{"action":"opened"}""", "issue.opened", "ignored");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fixture.EventAcceptance.LastSubmission!.SourceEventId.ShouldBeNull();
        fixture.EventAcceptance.LastSubmission!.IdempotencyKey.ShouldBeNull();
    }

    // The permission covers an absent value, never a present one: a request that does carry the
    // identity is still deduplicated by it.
    [Fact]
    public async Task PostWebhook_PresentIdentityUnderAllowMissing_StillIdentifiesTheEvent()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with
        {
            EventIdentityRule = new SourceEventIdentityRule
            {
                Kind = "json_path",
                Value = "/delivery/id",
                AllowMissing = true
            }
        };

        HttpResponseMessage response = await SendAsync(
            callbackId, """{"delivery":{"id":"body-delivery-2"},"action":"opened"}""", "issue.opened", "ignored");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fixture.EventAcceptance.LastSubmission!.SourceEventId.ShouldBe("body-delivery-2");
    }

    [Fact]
    public async Task PostWebhook_KnownIdentity_ReturnsBeforeCurrentMapping()
    {
        Guid callbackId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with
        {
            EventIdentityRule = new SourceEventIdentityRule { Kind = "header", Value = DeliveryIdHeaderName },
            SourceMapping = new TransformSpec("jsonata", "1", "$error(\"must not run\")")
        };
        fixture.EventAcceptance.ExistingBySourceEventId = new EventAcceptance
        {
            EventId = eventId,
            Status = EventStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
            AlreadyAccepted = true
        };

        HttpResponseMessage response = await SendAsync(callbackId, """{"action":"opened"}""", "issue.opened", "known-delivery");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("event_id").GetGuid().ShouldBe(eventId);
        fixture.EventAcceptance.LastSubmission.ShouldBeNull();
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
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.MediaType.ShouldBe("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe((int)HttpStatusCode.Unauthorized);
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("Signature verification failed.");
        string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("trace_id").GetString()).ShouldBeFalse();
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
    public async Task PostWebhook_BodyWithInvalidUtf8InAString_Returns400()
    {
        Guid callbackId = Guid.NewGuid();
        fixture.SourceEndpointResolver.Result = BuildResolvedEndpoint() with { SourceVerification = null };
        byte[] body = [.. "{\"action\":\"a"u8, 0x97, .. "b\"}"u8];
        HttpRequestMessage request = new(HttpMethod.Post, $"/webhooks/{callbackId}")
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } }
        };
        request.Headers.TryAddWithoutValidation(EventTypeHeaderName, "issue.opened");
        request.Headers.TryAddWithoutValidation(DeliveryIdHeaderName, "delivery-utf8");

        HttpResponseMessage response = await client.SendAsync(request);

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
        byte[] key = Encoding.UTF8.GetBytes(IngestionApiFixture.WebhookSecretValue);
        byte[] hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ResolvedSourceEndpoint BuildResolvedEndpoint() => new()
    {
        TenantId = Guid.NewGuid(),
        TenantSlug = IngestionApiFixture.WebhookTenantSlug,
        TopicId = Guid.NewGuid(),
        SourceId = Guid.NewGuid(),
        ConnectorKey = "test_webhook",
        SourceVerification = new SourceVerification
        {
            Scheme = "hmac_sha256",
            // Overrides the platform default (X-Hub-Signature-256 / "sha256=" prefix / hex) via
            // optional Config keys, proving the shape stays per-Connector-overridable.
            Config = JsonSerializer.Deserialize<JsonElement>($$"""{"header_name":"{{SignatureHeaderName}}","encoding":"hex","prefix":""}"""),
            SecretRefs = JsonSerializer.Deserialize<JsonElement>(
                $$"""{"secret":"{{IngestionApiFixture.WebhookSecretReference}}"}"""),
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
