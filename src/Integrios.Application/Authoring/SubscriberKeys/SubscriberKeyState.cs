using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.SubscriberKeys;

public static class SubscriberKeyState
{
    public static string From(SubscriberKey key) => key.RevokedAt is null ? "active" : "revoked";
}
