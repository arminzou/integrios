using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Client;
using Integrios.FunctionalTests.Worker;
using Integrios.Subscriber;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.FunctionalTests.Subscriber;

/// Drives the Integrios.Client SDK against the real Delivery API host and database.
public sealed class SubscriberSdkTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;
    private TestHostBuilder deliveryApi = null!;
    private string subscriberKey = null!;
    private IHost? subscriber;

    public SubscriberSdkTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.SeedPullDestinationsAsync();
        // A three-second lease makes the SDK renew about every second.
        deliveryApi = new TestHostBuilder(SubscriberHost.CreateBuilder, SubscriberHost.ConfigurePipeline)
            .WithSettings(fixture.Configuration)
            .WithSetting("Integrios:Delivery:HttpTimeout", "00:00:01")
            .WithSetting("Integrios:Delivery:AttemptDeadline", "00:00:02")
            .WithSetting("Integrios:Delivery:LeaseDuration", "00:00:03")
            .WithSetting("Integrios:Delivery:ShutdownGracePeriod", "00:00:05");
        subscriberKey = await fixture.SeedSubscriberKeyAsync(WorkerRoutingFixture.PullDestinationAId);
    }

    public async Task DisposeAsync()
    {
        if (subscriber is not null)
        {
            await subscriber.StopAsync();
            subscriber.Dispose();
        }
        deliveryApi.Dispose();
    }

    private async Task StartSubscriberAsync(
        Func<Delivery, CancellationToken, Task<DeliveryResult>> handle, int concurrency = 1)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new DelegateHandler(handle));
        builder.Services
            .AddIntegriosSubscriber<DelegateHandler>(options =>
            {
                options.BaseAddress = new Uri("http://subscriber");
                options.SubscriberKey = subscriberKey;
                options.Concurrency = concurrency;
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                ((TestServer)deliveryApi.Services.GetRequiredService<IServer>()).CreateHandler());
        subscriber = builder.Build();
        await subscriber.StartAsync();
    }

    /// Waits for the delivery to reach the status after its first attempt.
    private async Task<EventDeliveryState> WaitForStatusAsync(Guid deliveryId, string status)
    {
        for (int i = 0; i < 100; i++)
        {
            EventDeliveryState state = await fixture.GetEventDeliveryAsync(deliveryId);
            if (state.Status == status && state.LifetimeAttemptCount > 0)
                return state;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Delivery {deliveryId} never reached {status}.");
    }

    [Fact]
    public async Task AReturningHandlerAcksTheMappedDelivery()
    {
        var received = new TaskCompletionSource<Delivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartSubscriberAsync((delivery, _) =>
        {
            received.TrySetResult(delivery);
            return Task.FromResult(DeliveryResult.Ack);
        });

        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        await WaitForStatusAsync(deliveryId, "succeeded");
        Delivery delivery = await received.Task;
        delivery.DeliveryId.ShouldBe(deliveryId);
        delivery.EventType.ShouldBe("order.placed");
        delivery.Payload.GetRawText().ShouldBe("""{"mapped":true}""");
    }

    [Fact]
    public async Task ARetryResultSchedulesTheRetry()
    {
        await StartSubscriberAsync((_, _) =>
            Task.FromResult(DeliveryResult.Retry(TimeSpan.FromMinutes(5), "provider unavailable")));

        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        EventDeliveryState state = await WaitForStatusAsync(deliveryId, "pending");
        state.DeliverAfter!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(4));
        (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldHaveSingleItem().ErrorMessage.ShouldBe("provider unavailable");
    }

    [Fact]
    public async Task APermanentFailureDeadLetters()
    {
        await StartSubscriberAsync((_, _) => throw new PermanentDeliveryFailureException("order rejected"));

        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        await WaitForStatusAsync(deliveryId, "dead_lettered");
        (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldHaveSingleItem().ErrorMessage.ShouldBe("order rejected");
    }

    [Fact]
    public async Task AnUnexpectedExceptionSchedulesThePlatformRetry()
    {
        await StartSubscriberAsync((_, _) => throw new InvalidOperationException("provider unavailable"));
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        await WaitForStatusAsync(deliveryId, "pending");

        (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldHaveSingleItem().ErrorMessage.ShouldBe("provider unavailable");
    }

    [Fact]
    public async Task StoppingTheHostCancelsTheHandlerAndSchedulesRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartSubscriberAsync(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return DeliveryResult.Ack;
        });
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await subscriber!.StopAsync();

        await WaitForStatusAsync(deliveryId, "pending");
        (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldHaveSingleItem().Status.ShouldBe("failed");
    }

    [Fact]
    public async Task AHandlerOutlivingItsFirstLeaseKeepsTheDelivery()
    {
        // The second claim loop would reclaim the delivery if its lease were allowed to lapse.
        await StartSubscriberAsync(
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return DeliveryResult.Ack;
            },
            concurrency: 2);

        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        EventDeliveryState state = await WaitForStatusAsync(deliveryId, "succeeded");
        state.LifetimeAttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task AReclaimedDeliveryCancelsTheHandlerAndIsNotSettled()
    {
        var started = new TaskCompletionSource<Delivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartSubscriberAsync(async (delivery, token) =>
        {
            started.TrySetResult(delivery);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancelled.TrySetResult();
            return DeliveryResult.Ack;
        });
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");
        Delivery first = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using HttpClient other = deliveryApi.CreateClient();
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", subscriberKey);
        HttpResponseMessage reclaim;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // The SDK's own renew can extend the expired lease before the reclaim lands, so retry the race.
        do
        {
            await fixture.ForceLeaseExpiredAsync(deliveryId);
            reclaim = await other.PostAsJsonAsync("/delivery/v1/deliveries:claim", new { max = 1, wait_seconds = 0 }, timeout.Token);
            if (reclaim.StatusCode == HttpStatusCode.NoContent)
                reclaim.Dispose();
        }
        while (reclaim.StatusCode == HttpStatusCode.NoContent);
        JsonElement second = (await reclaim.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deliveries").EnumerateArray().ShouldHaveSingleItem();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        EventDeliveryState state = await fixture.GetEventDeliveryAsync(deliveryId);
        state.Status.ShouldBe("in_flight");
        state.ActiveAttemptId!.Value.ToString("N").ShouldBe(second.GetProperty("lease_token").GetString());
        (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldNotContain(
            attempt => attempt.Id.ToString("N") == first.LeaseToken && attempt.Status == "succeeded");
    }

    private sealed class DelegateHandler(Func<Delivery, CancellationToken, Task<DeliveryResult>> handle) : IDeliveryHandler
    {
        public Task<DeliveryResult> HandleAsync(Delivery delivery, CancellationToken cancellationToken) =>
            handle(delivery, cancellationToken);
    }
}
