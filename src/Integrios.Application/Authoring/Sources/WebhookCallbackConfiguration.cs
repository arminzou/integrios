using System.Text.Json;

namespace Integrios.Application.Authoring.Sources;

internal static class WebhookCallbackConfiguration
{
    public static JsonElement WithCallbackId(JsonElement configuration, Guid callbackId)
    {
        var values = configuration.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
        values["callback_id"] = JsonSerializer.SerializeToElement(callbackId);
        return JsonSerializer.SerializeToElement(values);
    }

    public static Guid ExistingOrNew(JsonElement configuration) =>
        configuration.TryGetProperty("callback_id", out JsonElement value) && value.TryGetGuid(out Guid callbackId)
            ? callbackId
            : Guid.NewGuid();
}
