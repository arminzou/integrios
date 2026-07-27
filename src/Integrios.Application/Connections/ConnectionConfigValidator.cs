using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Connections;

internal static class ConnectionConfigValidator
{
    public static void ValidateDestination(Integration integration, JsonElement config)
    {
        if (integration.Direction == IntegrationDirection.Source)
            return;

        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("url", out JsonElement urlElement)
            || urlElement.ValueKind != JsonValueKind.String
            || !OutboundHttpDestination.TryParse(urlElement.GetString(), out _))
        {
            throw new ConnectionRequestValidationException(
                "Connection config must contain an absolute HTTP or HTTPS 'url'.");
        }
    }
}
