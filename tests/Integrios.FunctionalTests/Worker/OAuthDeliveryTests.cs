using System.Net;
using System.Text;
using System.Text.Json;

namespace Integrios.FunctionalTests.Worker;

public sealed class OAuthDeliveryTests(WorkerRoutingFixture fixture) : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    public async Task Worker_AcquiresAndReusesOAuthToken(string method)
    {
        const string secretReference = "oauth_client_secret";
        const string clientSecret = "client-secret-canary";
        const string accessToken = "access-token-canary";
        string clientId = $"client-{method}";
        fixture.SecretResolver.Set(secretReference, clientSecret);
        fixture.OAuthTokenEndpoint.EnqueueToken(accessToken);
        await fixture.UpdateLedgerExecutionConfigurationAsync(
            WorkerRoutingFixture.LedgerSinkUrl,
            Authentication(method, clientId, secretReference, "orders.write deliveries.read"),
            "webhook");
        Guid firstEvent = await fixture.InsertEventAndOutboxAsync("payment.created");
        Guid secondEvent = await fixture.InsertEventAndOutboxAsync("payment.created");

        (await fixture.RunWorkerBatchAsync()).ShouldBe(2);

        TokenRequest request = fixture.OAuthTokenEndpoint.Calls.ShouldHaveSingleItem();
        if (method == "client_secret_basic")
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Authorization.ShouldBe($"Basic {encoded}");
            request.Body.ShouldBe("grant_type=client_credentials&scope=orders.write+deliveries.read");
        }
        else
        {
            request.Authorization.ShouldBeNull();
            request.Body.ShouldBe(
                $"grant_type=client_credentials&scope=orders.write+deliveries.read&client_id={clientId}&client_secret={clientSecret}");
        }
        fixture.DeliveryClient.Calls.Count.ShouldBe(2);
        fixture.DeliveryClient.Calls.ShouldAllBe(call => call.Headers["Authorization"] == $"Bearer {accessToken}");
        string persisted = JsonSerializer.Serialize(new[]
        {
            await fixture.GetEventDetailsAsync(firstEvent),
            await fixture.GetEventDetailsAsync(secondEvent)
        });
        persisted.ShouldNotContain(clientSecret, Case.Sensitive);
        persisted.ShouldNotContain(accessToken, Case.Sensitive);
    }

    [Fact]
    public async Task Worker_TransientOAuthFailureUsesNormalRetryAndDoesNotSendDestinationRequest()
    {
        const string clientSecret = "transient-client-secret-canary";
        const string responseCanary = "transient-response-canary";
        fixture.SecretResolver.Set("transient_secret", clientSecret);
        fixture.OAuthTokenEndpoint.EnqueueFailure(
            HttpStatusCode.BadRequest,
            $$"""{"error":"temporarily_unavailable","detail":"{{responseCanary}}"}""",
            TimeSpan.FromHours(1));
        await fixture.UpdateLedgerExecutionConfigurationAsync(
            WorkerRoutingFixture.LedgerSinkUrl,
            Authentication("client_secret_post", "transient-client", "transient_secret"),
            "webhook");
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        (await fixture.RunWorkerBatchAsync()).ShouldBe(1);

        fixture.DeliveryClient.Calls.ShouldBeEmpty();
        EventDeliveryState delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe("pending");
        delivery.DeliverAfter.ShouldNotBeNull();
        delivery.DeliverAfter.Value.ShouldBeInRange(
            DateTimeOffset.UtcNow.AddMinutes(14),
            DateTimeOffset.UtcNow.AddMinutes(16));
        DeliveryAttemptState firstAttempt = (await fixture.GetDeliveryAttemptsAsync(delivery.Id)).ShouldHaveSingleItem();
        firstAttempt.FailurePhase.ShouldBe("authentication");
        firstAttempt.ErrorMessage.ShouldNotBeNull();
        firstAttempt.ErrorMessage.ShouldNotContain(clientSecret, Case.Sensitive);
        firstAttempt.ErrorMessage.ShouldNotContain(responseCanary, Case.Sensitive);

        fixture.OAuthTokenEndpoint.EnqueueToken("recovered-access-token");
        await fixture.ForceDeliveryRetryNowAsync(eventId);
        (await fixture.RunDeliveryBatchAsync()).ShouldBe(1);

        fixture.OAuthTokenEndpoint.Calls.Count.ShouldBe(2);
        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();
        (await fixture.GetDeliveryAttemptsAsync(delivery.Id)).Count.ShouldBe(2);
        (await fixture.GetEventDeliveryAsync(delivery.Id)).Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Worker_TerminalOAuthFailureDeadLettersWithoutSendingDestinationRequest()
    {
        const string clientSecret = "terminal-client-secret-canary";
        const string responseCanary = "terminal-response-canary";
        fixture.SecretResolver.Set("terminal_secret", clientSecret);
        fixture.OAuthTokenEndpoint.EnqueueFailure(
            HttpStatusCode.Unauthorized,
            $$"""{"error":"invalid_client","detail":"{{responseCanary}}"}"""
        );
        await fixture.UpdateLedgerExecutionConfigurationAsync(
            WorkerRoutingFixture.LedgerSinkUrl,
            Authentication("client_secret_basic", "terminal-client", "terminal_secret"),
            "webhook");
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        (await fixture.RunWorkerBatchAsync()).ShouldBe(1);

        fixture.DeliveryClient.Calls.ShouldBeEmpty();
        EventDeliveryState delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe("dead_lettered");
        DeliveryAttemptState attempt = (await fixture.GetDeliveryAttemptsAsync(delivery.Id)).ShouldHaveSingleItem();
        attempt.FailurePhase.ShouldBe("authentication");
        attempt.ErrorMessage.ShouldNotBeNull();
        attempt.ErrorMessage.ShouldNotContain(clientSecret, Case.Sensitive);
        attempt.ErrorMessage.ShouldNotContain(responseCanary, Case.Sensitive);
    }

    private static string Authentication(string method, string clientId, string secretReference, string? scope = null) =>
        JsonSerializer.Serialize(new
        {
            scheme = "oauth2_client_credentials",
            config = new { token_endpoint = "https://identity.example/token", client_id = clientId, client_auth_method = method, scope },
            secret_refs = new { client_secret = secretReference }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
}
