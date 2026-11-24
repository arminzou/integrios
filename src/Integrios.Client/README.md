# Integrios.Client

Consume an Integrios pull Destination from a .NET generic host. The SDK claims deliveries from the
Delivery API, renews their leases while your handler runs, and settles each one with the result
your handler returns.

## Compatibility

Requires an Integrios server at 0.13.0 or later, the first release with the Delivery API. Before
1.0, a server release can change the Delivery API; the changelog names the server version each
client release needs.

```csharp
using Integrios.Client;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddIntegriosSubscriber<OrderHandler>(options =>
{
    options.BaseAddress = new Uri("http://integrios-subscriber:5241/");
    options.SubscriberKey = builder.Configuration["Integrios:SubscriberKey"]!;
    options.Concurrency = 4;
});
builder.Build().Run();

sealed class OrderHandler : IDeliveryHandler
{
    public async Task<DeliveryResult> HandleAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        // delivery.Payload is the Event's JSON payload.
        return DeliveryResult.Ack;
    }
}
```

A handler decides each delivery's outcome:

- Return `DeliveryResult.Ack` when it succeeded.
- Return `DeliveryResult.Retry(after, reason)` to have Integrios deliver it again. Leave `after`
  null to use the platform's backoff.
- Throw `PermanentDeliveryFailureException` to dead-letter it.
- Any other exception retries it under the platform's backoff.

The cancellation token fires when the host stops or the attempt loses ownership of the delivery.
Integrios may deliver an Event more than once, so make handlers idempotent.
