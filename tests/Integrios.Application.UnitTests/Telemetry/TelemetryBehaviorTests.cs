using System.Diagnostics;
using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Application.Telemetry;
using MediatR;
using Integrios.Tests.Shared;

namespace Integrios.Application.UnitTests;

[Collection(ActivityTestCollection.Name)]
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
        span.StatusDescription.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_StartsRootAcceptanceSpanForEveryIngressCommand()
    {
        using var collector = new ActivityCollector(ActivitySources.ApplicationName);
        var accepted = new IngestEventResult
        {
            EventId = Guid.NewGuid(),
            Status = Domain.Enums.EventStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
            AlreadyAccepted = false
        };

        await HandleAcceptanceAsync(new IngestEventCommand(Guid.NewGuid(), Guid.NewGuid(), JsonDocument.Parse("{}").RootElement), accepted);
        await HandleAcceptanceAsync(new AcceptVerifiedWebhookCommand(Guid.NewGuid(), null, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty), accepted);
        await HandleAcceptanceAsync(new AcceptQueueMessageCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, JsonDocument.Parse("{}").RootElement), accepted);

        Activity[] spans = collector.Activities.Where(activity => activity.OperationName == "event.accept").ToArray();
        spans.Length.ShouldBe(3);
        spans.ShouldAllBe(span => span.ParentId == null);
    }

    [Fact]
    public void TryParseTraceparent_ReturnsOnlyValidW3CContext()
    {
        ActivitySources.TryParseTraceparent(
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", out var context).ShouldBeTrue();
        context.TraceId.ToString().ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        ActivitySources.TryParseTraceparent("not-a-traceparent", out _).ShouldBeFalse();
    }

    private static Task HandleAcceptanceAsync<TRequest>(TRequest request, IngestEventResult accepted)
        where TRequest : IRequest<IngestEventResult> =>
        new TelemetryBehavior<TRequest, IngestEventResult>().Handle(
            request,
            _ => Task.FromResult(accepted),
            CancellationToken.None);
}
