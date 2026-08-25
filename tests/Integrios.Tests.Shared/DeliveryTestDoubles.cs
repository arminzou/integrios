using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Tests.Shared;

public static class DeliveryTestDoubles
{
    public static EventDeliveryWorkItem MakeWorkItem(
        Guid? id = null,
        Guid? eventId = null,
        Guid? subscriptionId = null,
        Guid? destinationConnectionId = null,
        Guid? tenantId = null,
        Guid? attemptId = null,
        int attemptNumber = 1,
        string url = "https://erp.example/webhook",
        string payload = "{\"amount\":42}",
        string? transform = null,
        string connectorKey = "erp_system",
        string? traceparent = null) =>
        new(
            id ?? Guid.NewGuid(),
            attemptId ?? Guid.NewGuid(),
            attemptNumber,
            eventId ?? Guid.NewGuid(),
            subscriptionId ?? Guid.NewGuid(),
            destinationConnectionId ?? Guid.NewGuid(),
            tenantId ?? Guid.NewGuid(),
            "test-tenant",
            payload,
            "payment.created",
            "payments",
            DateTimeOffset.UtcNow,
            transform,
            connectorKey,
            "{\"version\":1,\"base_uri\":\"" + url + "\",\"request\":{\"version\":1,\"method\":\"POST\",\"headers\":{},\"body\":\"json\"}}",
            traceparent);
}

public sealed class FakeEventDeliveryQueue : IEventDeliveryQueue
{
    public IReadOnlyList<EventDeliveryWorkItem> ClaimedItems { get; init; } = [];
    public IReadOnlyList<EventDeliveryClaimResult>? ClaimResults { get; init; }
    public EventDeliveryDisposition FailureDisposition { get; set; } = EventDeliveryDisposition.RetryScheduled;
    public DeliveryFinalizationStatus FinalizationStatus { get; set; } = DeliveryFinalizationStatus.Applied;
    public List<DeliveryAttemptCompletion> Completions { get; } = [];
    public List<DeliveryFinalizationResult> Finalizations { get; } = [];
    public Queue<Exception> FinalizationExceptions { get; init; } = [];
    public bool HonorFinalizationCancellation { get; init; }
    public List<string>? Operations { get; init; }
    public TaskCompletionSource? FinalizationSignal { get; init; }
    public int ClaimCallCount { get; private set; }
    private int claimIndex;

    public Task<EventDeliveryClaimResult?> ClaimNextWithRecoveryAsync(CancellationToken cancellationToken = default)
    {
        ClaimCallCount++;
        Operations?.Add("claim");
        if (ClaimResults is not null)
        {
            return Task.FromResult<EventDeliveryClaimResult?>(
                claimIndex < ClaimResults.Count ? ClaimResults[claimIndex++] : null);
        }

        return Task.FromResult<EventDeliveryClaimResult?>(
            claimIndex < ClaimedItems.Count
                ? new ClaimedEventDelivery(ClaimedItems[claimIndex++])
                : null);
    }

    public Task<DeliveryFinalizationResult> FinalizeAsync(DeliveryAttemptCompletion completion, CancellationToken cancellationToken = default)
    {
        Completions.Add(completion);
        Operations?.Add("finalize");
        FinalizationSignal?.TrySetResult();
        if (HonorFinalizationCancellation)
            cancellationToken.ThrowIfCancellationRequested();
        if (FinalizationExceptions.TryDequeue(out Exception? exception))
            throw exception;

        var disposition = completion.Succeeded ? EventDeliveryDisposition.Succeeded : FailureDisposition;
        var result = FinalizationStatus == DeliveryFinalizationStatus.Applied
            ? new DeliveryFinalizationResult(FinalizationStatus, disposition)
            : new DeliveryFinalizationResult(FinalizationStatus);
        Finalizations.Add(result);
        return Task.FromResult(result);
    }

}

public sealed class FakeDeliveryClient(
    DeliveryResult result,
    List<string>? capturedPayloads = null,
    List<string>? operations = null) : IDeliveryClient
{
    public List<string> DeliveredUrls { get; } = [];

    public Task<DeliveryResult> DeliverAsync(
        OutboundHttpMessage request, HttpSuccessRule? successRule, CancellationToken cancellationToken = default)
    {
        _ = successRule;
        _ = cancellationToken;
        operations?.Add("deliver");
        DeliveredUrls.Add(request.Uri);
        capturedPayloads?.Add(request.JsonBody ?? string.Empty);
        return Task.FromResult(result);
    }
}

public sealed class NullSecretResolver : IDestinationAuthenticationSecretResolver
{
    public string ProviderName => "test";

    public Task<string> ResolveAsync(TenantSecretScope tenant, string secretName, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException($"Unexpected secret lookup for '{secretName}'.");
}
