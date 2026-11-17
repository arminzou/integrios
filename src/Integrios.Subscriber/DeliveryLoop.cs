using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integrios.Subscriber;

/// Runs the configured number of claim loops. Each loop claims one delivery at a time, so
/// concurrency is also the number of leases this process holds.
internal sealed class DeliveryLoop<THandler>(
    IDeliveryTransport transport,
    IServiceScopeFactory scopes,
    int concurrency,
    TimeProvider time,
    ILogger<DeliveryLoop<THandler>> logger) : BackgroundService
    where THandler : IDeliveryHandler
{
    private const int ClaimWaitSeconds = 20;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => ClaimLoopAsync(stoppingToken)));

    private async Task ClaimLoopAsync(CancellationToken stoppingToken)
    {
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (Delivery delivery in await transport.ClaimAsync(1, ClaimWaitSeconds, stoppingToken))
                    await ProcessAsync(delivery, stoppingToken);
                failures = 0;
                continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            // Anything reaching here is the Delivery API or the network; a faulted loop would stop the host.
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Claiming from the Delivery API failed; retrying with backoff.");
            }
            await PauseAsync(Backoff(failures++), stoppingToken);
        }
    }

    private async Task ProcessAsync(Delivery delivery, CancellationToken stoppingToken)
    {
        using var handling = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewal = new CancellationTokenSource();
        Task<DateTimeOffset?> renewing = RenewAsync(delivery, handling, renewal.Token);
        Settlement settlement = await HandleAsync(delivery, handling.Token);
        await renewal.CancelAsync();
        if (await renewing is not { } leaseExpiresAt)
        {
            logger.LogWarning(
                "Delivery {DeliveryId} attempt {AttemptId} lost ownership; its outcome is not reported.",
                delivery.DeliveryId, delivery.AttemptId);
            return;
        }
        await SettleAsync(delivery, settlement, leaseExpiresAt, stoppingToken);
    }

    private async Task<Settlement> HandleAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            DeliveryResult result = await scope.ServiceProvider.GetRequiredService<THandler>()
                .HandleAsync(delivery, cancellationToken);
            return result.Settlement;
        }
        catch (PermanentDeliveryFailureException exception)
        {
            return Settlement.Fail(exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Handler failed delivery {DeliveryId}; it will be retried.", delivery.DeliveryId);
            return Settlement.Nack(null, exception.Message);
        }
    }

    /// Renews at a third of the remaining lease until stopped. Answers the last lease expiry, or
    /// null once ownership is lost, after cancelling the handler.
    private async Task<DateTimeOffset?> RenewAsync(
        Delivery delivery, CancellationTokenSource handling, CancellationToken stopRenewing)
    {
        DateTimeOffset leaseExpiresAt = delivery.LeaseExpiresAt;
        int failures = 0;
        while (true)
        {
            TimeSpan wait = failures == 0 ? (leaseExpiresAt - time.GetUtcNow()) / 3 : Backoff(failures - 1);
            TimeSpan remaining = leaseExpiresAt - time.GetUtcNow();
            await PauseAsync(wait < remaining ? wait : remaining, stopRenewing);
            if (stopRenewing.IsCancellationRequested)
                return time.GetUtcNow() < leaseExpiresAt ? leaseExpiresAt : null;
            if (time.GetUtcNow() >= leaseExpiresAt)
                break;
            try
            {
                using var deadline = new CancellationTokenSource(leaseExpiresAt - time.GetUtcNow(), time);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(stopRenewing, deadline.Token);
                if (await transport.RenewAsync(delivery, request.Token) is not { } renewed)
                    break;
                leaseExpiresAt = renewed;
                failures = 0;
            }
            catch (OperationCanceledException) when (stopRenewing.IsCancellationRequested)
            {
                return time.GetUtcNow() < leaseExpiresAt ? leaseExpiresAt : null;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Renewing delivery {DeliveryId} failed.", delivery.DeliveryId);
                failures++;
                if (time.GetUtcNow() >= leaseExpiresAt)
                    break;
            }
        }
        await handling.CancelAsync();
        return null;
    }

    /// Repeating a recorded settlement is safe, so transport failures retry while the lease holds.
    private async Task SettleAsync(
        Delivery delivery, Settlement settlement, DateTimeOffset leaseExpiresAt, CancellationToken stoppingToken)
    {
        for (int failures = 0; ; failures++)
        {
            TimeSpan remaining = leaseExpiresAt - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return;
            try
            {
                // Not the stopping token: an outcome reached during shutdown is still worth reporting.
                using var deadline = new CancellationTokenSource(remaining, time);
                if (!await transport.SettleAsync(delivery, settlement, deadline.Token))
                    logger.LogWarning(
                        "Delivery {DeliveryId} attempt {AttemptId} lost ownership before its outcome was recorded.",
                        delivery.DeliveryId, delivery.AttemptId);
                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Reporting delivery {DeliveryId} failed.", delivery.DeliveryId);
            }
            remaining = leaseExpiresAt - time.GetUtcNow();
            await PauseAsync(Backoff(failures) < remaining ? Backoff(failures) : remaining, stoppingToken);
        }
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures), MaxBackoff.TotalSeconds));

    private async Task PauseAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, time, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
