using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Application.PullDelivery;
using Integrios.Application.Telemetry;
using Integrios.Tests.Shared;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Integrios.Application.UnitTests.PullDelivery;

[Collection(TelemetryListenerCollection.Name)]
public sealed class SettlePullDeliveryCommandTests
{
    [Theory]
    [InlineData("ack", EventDeliveryDisposition.Succeeded, "integrios_deliveries_succeeded", null)]
    [InlineData("reject", EventDeliveryDisposition.DeadLettered, "integrios_deliveries_dead_lettered", null)]
    [InlineData("release", EventDeliveryDisposition.RetryScheduled, "integrios_deliveries_failed", "error")]
    [InlineData("release", EventDeliveryDisposition.DeadLettered, "integrios_deliveries_dead_lettered", null)]
    public async Task Settlement_RecordsOutcomeOnlyWhenFirstApplied(
        string action, EventDeliveryDisposition disposition, string instrument, string? httpStatusClass)
    {
        using var collector = new MetricCollector(IntegriosMetrics.MeterName);
        var owner = new PullAttemptOwner(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        PullSettlement settlement = action switch
        {
            "ack" => PullSettlement.Ack(),
            "reject" => PullSettlement.Reject("invalid payload"),
            "release" => PullSettlement.Release(null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        IPullDeliveryQueue queue = Substitute.For<IPullDeliveryQueue>();
        queue.SettleAsync(owner, settlement, Arg.Any<CancellationToken>())
            .Returns(
                new PullSettlementResult(PullSettlementStatus.Applied, disposition),
                new PullSettlementResult(PullSettlementStatus.AlreadyApplied),
                new PullSettlementResult(PullSettlementStatus.OwnershipLost));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSubscriberApplicationServices();
        services.AddSingleton(queue);
        using ServiceProvider provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var command = new SettlePullDeliveryCommand(owner, settlement);

        (await mediator.Send(command)).ShouldBe(PullSettlementStatus.Applied);
        MetricCollector.Measurement measurement = collector.ForInstrument(instrument).ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tag("connector_key").ShouldBe("pull");
        measurement.Tag("http_status_class").ShouldBe(httpStatusClass);

        (await mediator.Send(command)).ShouldBe(PullSettlementStatus.AlreadyApplied);
        (await mediator.Send(command)).ShouldBe(PullSettlementStatus.OwnershipLost);
        collector.ForInstrument(instrument).Sum(sample => sample.Value).ShouldBe(1);
        string[] outcomeCounters = ["integrios_deliveries_succeeded", "integrios_deliveries_failed", "integrios_deliveries_dead_lettered"];
        outcomeCounters.Where(counter => counter != instrument).SelectMany(collector.ForInstrument).ShouldBeEmpty();
    }
}
