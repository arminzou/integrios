namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record CreateSubscriberKeyResult
{
    public required SubscriberKeyDto SubscriberKey { get; init; }
    public required string Token { get; init; }
}
