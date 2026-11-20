using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.FunctionalTests.Worker;
using Integrios.Subscriber;
using Integrios.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Integrios.FunctionalTests.Subscriber;

public sealed class DeliveryProtocolTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private static readonly Guid A = WorkerRoutingFixture.PullDestinationAId;
    private static readonly Guid B = WorkerRoutingFixture.PullDestinationBId;
    private readonly WorkerRoutingFixture fixture;
    private TestHostBuilder host = null!;
    private HttpClient subscriberA = null!;
    private HttpClient subscriberB = null!;

    public DeliveryProtocolTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.SeedPullDestinationsAsync();
        host = new TestHostBuilder(SubscriberHost.CreateBuilder, SubscriberHost.ConfigurePipeline)
            .WithSettings(fixture.Configuration);
        subscriberA = Client(await fixture.SeedSubscriberKeyAsync(A));
        subscriberB = Client(await fixture.SeedSubscriberKeyAsync(B));
    }

    public Task DisposeAsync()
    {
        subscriberA.Dispose();
        subscriberB.Dispose();
        host.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client(string? token)
    {
        HttpClient client = host.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> Claim(HttpClient client, int max = 32, int waitSeconds = 0) =>
        client.PostAsJsonAsync("/delivery/v1/claims", new { max, wait_seconds = waitSeconds });

    private static async Task<JsonElement> ClaimOne(HttpClient client)
    {
        HttpResponseMessage response = await Claim(client);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("deliveries").EnumerateArray().ShouldHaveSingleItem();
    }

    private static string AttemptPath(JsonElement delivery, string action) =>
        $"/delivery/v1/deliveries/{delivery.GetProperty("delivery_id").GetGuid()}/attempts/{delivery.GetProperty("attempt_id").GetGuid()}/{action}";

    private static Task<HttpResponseMessage> Post(HttpClient client, JsonElement delivery, string action, object? body = null) =>
        body is null
            ? client.PostAsync(AttemptPath(delivery, action), null)
            : client.PostAsJsonAsync(AttemptPath(delivery, action), body);

    private static async Task ShouldBeConflict(HttpResponseMessage response, string code)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe(code);
    }

    private static Guid DeliveryId(JsonElement delivery) => delivery.GetProperty("delivery_id").GetGuid();

    [Fact]
    public async Task Claim_ReturnsTheMappedDeliveryOnTheWire()
    {
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        JsonElement delivery = await ClaimOne(subscriberA);

        DeliveryId(delivery).ShouldBe(deliveryId);
        delivery.GetProperty("attempt_number").GetInt32().ShouldBe(1);
        delivery.GetProperty("event_type").GetString().ShouldBe("order.placed");
        delivery.GetProperty("payload").GetRawText().ShouldBe("""{"mapped":true}""");
        delivery.GetProperty("event_id").GetGuid().ShouldNotBe(Guid.Empty);
        delivery.GetProperty("lease_expires_at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        delivery.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["delivery_id", "attempt_id", "attempt_number", "event_id", "event_type", "topic", "accepted_at", "payload", "lease_expires_at", "traceparent"]);
        EventDeliveryState state = await fixture.GetEventDeliveryAsync(deliveryId);
        state.Status.ShouldBe("in_flight");
        state.ActiveAttemptId.ShouldBe(delivery.GetProperty("attempt_id").GetGuid());
    }

    [Fact]
    public async Task Claim_AnswersNoContentWhenNothingIsClaimable()
    {
        HttpResponseMessage response = await Claim(subscriberA);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Claim_HonorsMax()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        await fixture.FanoutPullDeliveryAsync("order.placed");

        HttpResponseMessage response = await Claim(subscriberA, max: 1);

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deliveries").GetArrayLength().ShouldBe(1);
        (await ClaimOne(subscriberA)).GetProperty("attempt_number").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task LongPoll_WaitsTheFullWindowWhenNoWorkArrives()
    {
        var elapsed = Stopwatch.StartNew();

        HttpResponseMessage response = await Claim(subscriberA, waitSeconds: 2);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LongPoll_ReturnsWhenWorkArrives()
    {
        var elapsed = Stopwatch.StartNew();
        Task<HttpResponseMessage> poll = Claim(subscriberA, waitSeconds: 20);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        HttpResponseMessage response = await poll;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        DeliveryId(body.GetProperty("deliveries").EnumerateArray().ShouldHaveSingleItem()).ShouldBe(deliveryId);
    }

    [Fact]
    public async Task LongPoll_StopsWhenTheSubscriberDisconnects()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var elapsed = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => subscriberA.PostAsJsonAsync(
            "/delivery/v1/claims", new { max = 1, wait_seconds = 20 }, cancel.Token));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        (await fixture.GetEventDeliveryAsync(deliveryId)).LifetimeAttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task Renew_ExtendsTheLease()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        JsonElement delivery = await ClaimOne(subscriberA);
        await Task.Delay(50);

        HttpResponseMessage response = await Post(subscriberA, delivery, "renew");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        DateTimeOffset renewed = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("lease_expires_at").GetDateTimeOffset();
        renewed.ShouldBeGreaterThan(delivery.GetProperty("lease_expires_at").GetDateTimeOffset());
        (await fixture.GetEventDeliveryAsync(DeliveryId(delivery))).LeaseExpiresAt.ShouldBe(renewed);
    }

    [Fact]
    public async Task Renew_PastTheAttemptCeilingIsRefused()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        JsonElement delivery = await ClaimOne(subscriberA);
        await fixture.BackdateActiveAttemptAsync(DeliveryId(delivery), TimeSpan.FromMinutes(31));

        await ShouldBeConflict(await Post(subscriberA, delivery, "renew"), "max_attempt_duration");
    }

    [Fact]
    public async Task Ack_CompletesTheDeliveryAndARepeatedAckSucceeds()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        JsonElement delivery = await ClaimOne(subscriberA);

        (await Post(subscriberA, delivery, "ack")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Post(subscriberA, delivery, "ack")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ShouldBeConflict(await Post(subscriberA, delivery, "nack"), "ownership_lost");

        (await fixture.GetEventDeliveryAsync(DeliveryId(delivery))).Status.ShouldBe("succeeded");
        (await fixture.GetDeliveryAttemptsAsync(DeliveryId(delivery))).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Nack_SchedulesTheRetryAfterTheRequestedDelay()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        JsonElement delivery = await ClaimOne(subscriberA);

        (await Post(subscriberA, delivery, "nack", new { retry_after_seconds = 300, error = "provider unavailable" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        EventDeliveryState state = await fixture.GetEventDeliveryAsync(DeliveryId(delivery));
        state.Status.ShouldBe("pending");
        state.DeliverAfter!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(4));
        DeliveryAttemptState attempt = (await fixture.GetDeliveryAttemptsAsync(DeliveryId(delivery))).ShouldHaveSingleItem();
        attempt.FailurePhase.ShouldBe("subscriber");
        attempt.ErrorMessage.ShouldBe("provider unavailable");
    }

    [Fact]
    public async Task Nack_WithoutABodyUsesTheDefaultBackoff()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        JsonElement delivery = await ClaimOne(subscriberA);

        (await Post(subscriberA, delivery, "nack")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await fixture.GetEventDeliveryAsync(DeliveryId(delivery))).Status.ShouldBe("pending");
    }

    [Fact]
    public async Task Fail_DeadLettersTheDelivery()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        JsonElement delivery = await ClaimOne(subscriberA);

        (await Post(subscriberA, delivery, "reject", new { error = "order rejected" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Post(subscriberA, delivery, "reject", new { error = "order rejected" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await fixture.GetEventDeliveryAsync(DeliveryId(delivery))).Status.ShouldBe("dead_lettered");
        (await fixture.GetDeliveryAttemptsAsync(DeliveryId(delivery))).ShouldHaveSingleItem().ErrorMessage.ShouldBe("order rejected");
    }

    [Fact]
    public async Task AReclaimedAttempt_LosesOwnershipOfEveryCall()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        JsonElement first = await ClaimOne(subscriberA);
        await fixture.ForceLeaseExpiredAsync(DeliveryId(first));
        JsonElement second = await ClaimOne(subscriberA);

        await ShouldBeConflict(await Post(subscriberA, first, "renew"), "ownership_lost");
        await ShouldBeConflict(await Post(subscriberA, first, "ack"), "ownership_lost");
        await ShouldBeConflict(await Post(subscriberA, first, "nack"), "ownership_lost");
        await ShouldBeConflict(await Post(subscriberA, first, "reject", new { error = "late" }), "ownership_lost");
        (await Post(subscriberA, second, "ack")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AKey_SeesOnlyItsOwnDestination()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");

        (await Claim(subscriberB)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonElement delivery = await ClaimOne(subscriberA);

        await ShouldBeConflict(await Post(subscriberB, delivery, "renew"), "ownership_lost");
        await ShouldBeConflict(await Post(subscriberB, delivery, "ack"), "ownership_lost");
        await ShouldBeConflict(await Post(subscriberB, delivery, "reject", new { error = "not mine" }), "ownership_lost");
        (await fixture.GetEventDeliveryAsync(DeliveryId(delivery))).Status.ShouldBe("in_flight");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("intg_sub_0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task EveryCall_RequiresAValidSubscriberKey(string? token)
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        using HttpClient anonymous = Client(token);
        string attempt = $"/delivery/v1/deliveries/{Guid.NewGuid()}/attempts/{Guid.NewGuid()}";

        (await Claim(anonymous)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        foreach (string action in new[] { "renew", "ack", "nack", "reject" })
            (await anonymous.PostAsync($"{attempt}/{action}", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fixture.GetLastPolledAtAsync(A)).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"max":"a","wait_seconds":1}""")]
    [InlineData("")]
    public async Task Claim_RejectsUnreadableBodiesWithoutLoggingAnError(string body)
    {
        var logs = new CapturingLoggerProvider();
        using TestHostBuilder logged = host.WithServices(services => services.AddSingleton<ILoggerProvider>(logs));
        using HttpClient client = logged.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await fixture.SeedSubscriberKeyAsync(A));

        HttpResponseMessage response = await client.PostAsync(
            "/delivery/v1/claims", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        logs.Records.ShouldNotContain(record => record.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData("""{"max":0,"wait_seconds":0}""", "max")]
    [InlineData("""{"max":33,"wait_seconds":0}""", "max")]
    [InlineData("""{"max":1,"wait_seconds":21}""", "wait_seconds")]
    [InlineData("""{"max":1,"wait_seconds":-1}""", "wait_seconds")]
    [InlineData("""{"wait_seconds":0}""", "max")]
    public async Task Claim_RejectsOutOfRangeRequests(string body, string field)
    {
        HttpResponseMessage response = await subscriberA.PostAsync(
            "/delivery/v1/claims", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Settlement_RejectsInvalidBodiesWithoutSettling()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        JsonElement delivery = await ClaimOne(subscriberA);

        (await Post(subscriberA, delivery, "nack", new { retry_after_seconds = -1 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(subscriberA, delivery, "nack", new { retry_after_seconds = 901 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(subscriberA, delivery, "reject", new { error = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(subscriberA, delivery, "reject", new { error = new string('x', 2001) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(subscriberA, delivery, "reject")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await fixture.GetEventDeliveryAsync(DeliveryId(delivery))).Status.ShouldBe("in_flight");
    }

    [Fact]
    public async Task Claim_RecordsTheDestinationsLastPollAtMostOnceAMinute()
    {
        (await Claim(subscriberA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        DateTimeOffset first = (await fixture.GetLastPolledAtAsync(A)).ShouldNotBeNull();
        (await fixture.GetLastPolledAtAsync(B)).ShouldBeNull();

        await Claim(subscriberA);
        (await fixture.GetLastPolledAtAsync(A)).ShouldBe(first);

        await fixture.BackdateLastPolledAtAsync(A, TimeSpan.FromMinutes(2));
        DateTimeOffset backdated = (await fixture.GetLastPolledAtAsync(A))!.Value;
        await Claim(subscriberA);
        (await fixture.GetLastPolledAtAsync(A))!.Value.ShouldBeGreaterThan(backdated.AddSeconds(90));
    }
}
