using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Integrios.Domain.Enums;
using Integrios.Tests.Shared;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application.UnitTests;

public sealed class ProcessOutboxBatchCommandTests
{
    [Fact]
    public async Task ProcessOutboxBatchCommand_ProcessesCommittedFanoutResults()
    {
        var eventId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var fanout = new FakeOutboxFanout(
            [new OutboxFanoutResult(eventId, topicId, EventStatus.Routed, 2, 2)]);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IOutboxFanout>(fanout);
        });

        var processedCount = await mediator.Send(new ProcessOutboxBatchCommand(10));

        processedCount.ShouldBe(1);
        fanout.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task ProcessOutboxBatchCommand_NoMatchingSubscriptions_MarksUnroutedAndEmitsCounter()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var eventId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var fanout = new FakeOutboxFanout(
            [new OutboxFanoutResult(eventId, topicId, EventStatus.Unrouted, 0, 0)]);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IOutboxFanout>(fanout);
        });

        var processedCount = await mediator.Send(new ProcessOutboxBatchCommand(10));

        processedCount.ShouldBe(1);
        var unrouted = metrics.ForInstrument("integrios_events_unrouted").ShouldHaveSingleItem();
        unrouted.Value.ShouldBe(1);
    }

    private static IMediator BuildMediator(Action<IServiceCollection> registerTestDoubles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        registerTestDoubles(services);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private sealed class FakeOutboxFanout(IReadOnlyList<OutboxFanoutResult> results) : IOutboxFanout
    {
        private int index;

        public int CallCount { get; private set; }

        public Task<OutboxFanoutResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<OutboxFanoutResult?>(index < results.Count ? results[index++] : null);
        }
    }
}
