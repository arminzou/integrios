namespace Integrios.Client;

/// <summary>Options for an Integrios subscriber.</summary>
public sealed class SubscriberOptions
{
    /// <summary>The Delivery API's address, such as http://integrios-subscriber:5241/.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The SubscriberKey that selects this handler's pull Destination.</summary>
    public string SubscriberKey { get; set; } = "";

    /// <summary>How many deliveries this process handles at once.</summary>
    public int Concurrency { get; set; } = 1;
}
