using System.Threading.RateLimiting;

namespace Integrios.Admin.Auth;

internal sealed class OperatorPasswordRateLimiter : IAsyncDisposable
{
    internal const int AttemptsPerEmail = 5;
    internal const int AttemptsPerPeer = 20;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> emailLimiter = CreateLimiter(AttemptsPerEmail);
    private readonly PartitionedRateLimiter<string> peerLimiter = CreateLimiter(AttemptsPerPeer);

    public async ValueTask<OperatorPasswordRateLimitResult> TryAcquireAsync(
        string emailPartition,
        string peerPartition,
        CancellationToken cancellationToken)
    {
        using RateLimitLease emailLease = await emailLimiter.AcquireAsync(
            emailPartition,
            permitCount: 1,
            cancellationToken);
        using RateLimitLease peerLease = await peerLimiter.AcquireAsync(
            peerPartition,
            permitCount: 1,
            cancellationToken);
        return (emailLease.IsAcquired, peerLease.IsAcquired) switch
        {
            (true, true) => OperatorPasswordRateLimitResult.Acquired,
            (false, true) => OperatorPasswordRateLimitResult.EmailExceeded,
            (true, false) => OperatorPasswordRateLimitResult.PeerExceeded,
            _ => OperatorPasswordRateLimitResult.EmailAndPeerExceeded,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await emailLimiter.DisposeAsync();
        await peerLimiter.DisposeAsync();
    }

    private static PartitionedRateLimiter<string> CreateLimiter(int permitLimit) =>
        PartitionedRateLimiter.Create<string, string>(partition =>
            RateLimitPartition.GetFixedWindowLimiter(
                partition,
                _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = permitLimit,
                    QueueLimit = 0,
                    Window = Window,
                }));
}

internal enum OperatorPasswordRateLimitResult
{
    Acquired,
    EmailExceeded,
    PeerExceeded,
    EmailAndPeerExceeded,
}
