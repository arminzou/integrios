namespace Integrios.Subscriber;

public sealed class SubscriberOptions
{
    /// The Delivery API's address, such as http://integrios-delivery-api:5241/.
    public Uri? BaseAddress { get; set; }

    /// The SubscriberKey that selects this handler's pull Destination.
    public string SubscriberKey { get; set; } = "";

    /// How many deliveries this process handles at once.
    public int Concurrency { get; set; } = 1;
}
