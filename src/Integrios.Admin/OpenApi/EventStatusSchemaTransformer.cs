using System.Text.Json.Nodes;
using Integrios.Domain.Enums;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Integrios.Admin.OpenApi;

/// EventStatus travels as its canonical snake_case string, but that spelling lives in a JSON
/// converter the document generator cannot read, so the schema would otherwise be an untyped value
/// and the generated browser client would read Event status as `unknown`. The vocabulary is taken
/// from EventStatusMap so the document can never describe a status the API does not send.
public sealed class EventStatusSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type != typeof(EventStatus))
            return Task.CompletedTask;

        schema.Type = JsonSchemaType.String;
        schema.Enum = [.. EventStatusMap.DbValues.Select(value => (JsonNode)JsonValue.Create(value))];
        return Task.CompletedTask;
    }
}
