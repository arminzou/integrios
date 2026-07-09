using Integrios.Domain.Integrations;

namespace Integrios.Application.Bootstrap;

public sealed record BuiltinIntegration(
    Guid Id,
    string Key,
    string Name,
    IntegrationDirection Direction,
    IReadOnlyList<string> SupportedAuthSchemes,
    string? Description);

public static class BuiltinCatalog
{
    public static readonly Guid WebhookId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static readonly IReadOnlyList<BuiltinIntegration> All =
    [
        new BuiltinIntegration(
            WebhookId,
            "webhook",
            "Webhook",
            IntegrationDirection.Both,
            [],
            "Generic webhook source or destination over HTTP."),
    ];
}
