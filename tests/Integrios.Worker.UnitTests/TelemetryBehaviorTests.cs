using System.Diagnostics;
using Integrios.Application.Telemetry;
using MediatR;

namespace Integrios.Worker.UnitTests;

public sealed class TelemetryBehaviorTests
{
    private sealed record PingRequest : IRequest<string>;

    [Fact]
    public async Task Handle_StartsSpanNamedAfterRequestType()
    {
        using var collector = new ActivityCollector(ActivitySources.ApplicationName);
        var behavior = new TelemetryBehavior<PingRequest, string>();

        var result = await behavior.Handle(new PingRequest(), _ => Task.FromResult("pong"), CancellationToken.None);

        result.ShouldBe("pong");
        var span = collector.Single("PingRequest");
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task Handle_MarksSpanError_WhenHandlerThrows()
    {
        using var collector = new ActivityCollector(ActivitySources.ApplicationName);
        var behavior = new TelemetryBehavior<PingRequest, string>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            behavior.Handle(new PingRequest(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        var span = collector.Single("PingRequest");
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }
}
