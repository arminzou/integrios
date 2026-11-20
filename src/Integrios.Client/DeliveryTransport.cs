using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Integrios.Client;

/// The Delivery API's calls. An attempt that lost ownership (409) answers null or false; any other
/// failure throws.
internal interface IDeliveryTransport
{
    Task<IReadOnlyList<Delivery>> ClaimAsync(int max, int waitSeconds, CancellationToken cancellationToken);

    Task<DateTimeOffset?> RenewAsync(Delivery delivery, CancellationToken cancellationToken);

    Task<bool> SettleAsync(Delivery delivery, Settlement settlement, CancellationToken cancellationToken);
}

internal sealed class HttpDeliveryTransport(HttpClient client) : IDeliveryTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<Delivery>> ClaimAsync(int max, int waitSeconds, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "delivery/v1/deliveries:claim", new ClaimBody(max, waitSeconds), Json, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return [];
        response.EnsureSuccessStatusCode();
        ClaimBatch? batch = await response.Content.ReadFromJsonAsync<ClaimBatch>(Json, cancellationToken);
        return batch!.Deliveries;
    }

    public async Task<DateTimeOffset?> RenewAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            DeliveryPath(delivery, "renew"), new LeaseBody(delivery.LeaseToken), Json, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return null;
        response.EnsureSuccessStatusCode();
        RenewedLease? lease = await response.Content.ReadFromJsonAsync<RenewedLease>(Json, cancellationToken);
        return lease!.LeaseExpiresAt;
    }

    public async Task<bool> SettleAsync(Delivery delivery, Settlement settlement, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            DeliveryPath(delivery, settlement.Action),
            new SettlementBody(delivery.LeaseToken, settlement.RetryAfterSeconds, settlement.Error),
            Json,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private static string DeliveryPath(Delivery delivery, string verb) =>
        $"delivery/v1/deliveries/{delivery.DeliveryId}:{verb}";

    private sealed record ClaimBody(int Max, int WaitSeconds);

    private sealed record ClaimBatch(IReadOnlyList<Delivery> Deliveries);

    private sealed record RenewedLease(DateTimeOffset LeaseExpiresAt);

    private sealed record LeaseBody(string LeaseToken);

    private sealed record SettlementBody(string LeaseToken, int? RetryAfterSeconds, string? Error);
}
