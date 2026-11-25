using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Application.PullDelivery;
using Integrios.Application.Transforms;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ClaimedDelivery = Integrios.Application.PullDelivery.PullDelivery;

namespace Integrios.Application.UnitTests.PullDelivery;

public sealed class ClaimPullDeliveriesCommandTests
{
    private static readonly Guid DestinationId = Guid.NewGuid();

    [Theory]
    [MemberData(nameof(LaterClaimFailures))]
    public async Task ALaterClaimFailure_ReturnsTheDeliveriesAlreadyLeased(Exception failure)
    {
        PullDeliveryWorkItem first = WorkItem();
        IPullDeliveryQueue queue = Substitute.For<IPullDeliveryQueue>();
        queue.ClaimNextAsync(DestinationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EventDeliveryClaimResult?>(new ClaimedPullDelivery(first)), Task.FromException<EventDeliveryClaimResult?>(failure));

        IReadOnlyList<ClaimedDelivery> claimed = await Send(queue, max: 5);

        claimed.ShouldHaveSingleItem().DeliveryId.ShouldBe(first.Id);
    }

    [Fact]
    public async Task AFailedMappingSettlement_ReturnsTheDeliveriesAlreadyLeased()
    {
        PullDeliveryWorkItem first = WorkItem();
        PullDeliveryWorkItem broken = WorkItem() with
        {
            MappingConfigSnapshot = """{"engine":"jsonata","version":"1","expression":"broken"}"""
        };
        IPullDeliveryQueue queue = Substitute.For<IPullDeliveryQueue>();
        queue.ClaimNextAsync(DestinationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EventDeliveryClaimResult?>(new ClaimedPullDelivery(first)), Task.FromResult<EventDeliveryClaimResult?>(new ClaimedPullDelivery(broken)));
        queue.SettleAsync(Arg.Any<PullAttemptOwner>(), Arg.Any<PullSettlement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PullSettlementResult>(new TimeoutException("database timeout")));

        IReadOnlyList<ClaimedDelivery> claimed = await Send(queue, max: 5, FailingTransforms());

        claimed.ShouldHaveSingleItem().DeliveryId.ShouldBe(first.Id);
    }

    [Fact]
    public async Task AFailureBeforeAnyClaim_Propagates()
    {
        IPullDeliveryQueue queue = Substitute.For<IPullDeliveryQueue>();
        queue.ClaimNextAsync(DestinationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EventDeliveryClaimResult?>(new TimeoutException("database timeout")));

        await Should.ThrowAsync<TimeoutException>(() => Send(queue, max: 5));
    }

    public static TheoryData<Exception> LaterClaimFailures => new()
    {
        new TimeoutException("database timeout"),
        new OperationCanceledException()
    };

    private static async Task<IReadOnlyList<ClaimedDelivery>> Send(
        IPullDeliveryQueue queue,
        int max,
        ITransformEvaluator? transforms = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddSingleton(queue);
        services.AddSingleton(Substitute.For<IDestinationPollRecorder>());
        services.AddSingleton(transforms ?? Substitute.For<ITransformEvaluator>());
        await using ServiceProvider provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IMediator>().Send(new ClaimPullDeliveriesCommand(DestinationId, max, TimeSpan.Zero));
    }

    private static ITransformEvaluator FailingTransforms()
    {
        ITransformEvaluator transforms = Substitute.For<ITransformEvaluator>();
        transforms.Evaluate(Arg.Any<TransformSpec>(), Arg.Any<string>(), Arg.Any<TransformContext>())
            .Returns(_ => throw new TransformEvaluationException("forced"));
        return transforms;
    }

    private static PullDeliveryWorkItem WorkItem() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        1,
        Guid.NewGuid(),
        Guid.NewGuid(),
        """{"id":1}""",
        "order.placed",
        "orders",
        DateTimeOffset.UtcNow,
        null,
        DateTimeOffset.UtcNow.AddMinutes(2),
        null);
}
