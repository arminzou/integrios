using Integrios.Infrastructure.Delivery;

namespace Integrios.Infrastructure.UnitTests.Delivery;

public sealed class CompletedHistoryCleanupTests
{
    [Fact]
    public async Task LeaseReleaseFailure_DoesNotMaskSweepFailure_AndDiscardsPool()
    {
        bool discarded = false;

        await CompletedHistoryLease.ReleaseAsync(
            () => Task.FromException(new InvalidOperationException("release")),
            () => discarded = true,
            new InvalidOperationException("sweep"));

        discarded.ShouldBeTrue();
    }

    [Fact]
    public async Task LeaseReleaseFailure_WithoutSweepFailure_IsReported_AndDiscardsPool()
    {
        bool discarded = false;

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            CompletedHistoryLease.ReleaseAsync(
                () => Task.FromException(new InvalidOperationException("release")),
                () => discarded = true,
                sweepFailure: null));

        exception.Message.ShouldBe("release");
        discarded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void SqlServerLockResult_AllowsSuccessAndTimeout(int result) =>
        SqlServerCompletedHistoryCleanup.ThrowIfLockFailed(result, CancellationToken.None);

    [Theory]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public void SqlServerLockResult_RejectsNonTimeoutFailures(int result)
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            SqlServerCompletedHistoryCleanup.ThrowIfLockFailed(result, CancellationToken.None));

        exception.Message.ShouldContain(result.ToString());
    }

    [Fact]
    public void SqlServerLockResult_PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            SqlServerCompletedHistoryCleanup.ThrowIfLockFailed(-2, cancellation.Token));
    }
}
