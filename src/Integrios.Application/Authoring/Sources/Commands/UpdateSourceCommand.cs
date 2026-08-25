using System.Text.Json;
using System.Text.Json.Nodes;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record UpdateSourceCommand(Guid TenantId, Guid Id, JsonElement Configuration) : IRequest<SourceDto?>;

internal sealed class UpdateSourceCommandHandler(ISourceRepository sourceRepository, IConnectionRepository connectionRepository, IConnectorReader connectorReader)
    : IRequestHandler<UpdateSourceCommand, SourceDto?>
{
    public async Task<SourceDto?> Handle(UpdateSourceCommand command, CancellationToken cancellationToken)
    {
        Source? source = await sourceRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (source is null || source.Status != Domain.Enums.SourceStatus.Active)
            return null;
        Connection connection = await connectionRepository.GetByIdAsync(command.TenantId, source.ConnectionId, cancellationToken)
            ?? throw new SourceValidationException("Source Connection must exist in the same Tenant.");
        Connector connector = await connectorReader.GetByIdAsync(connection.ConnectorId, cancellationToken)
            ?? throw new SourceValidationException("Source Connection references a Connector that does not exist.");
        SourceAuthoringValidator.Validate(source.Type, command.Configuration, connection, connector);
        JsonElement configuration = source.Type == Domain.Enums.SourceType.Webhook
            ? PreserveWebhookCallbackId(source.Configuration, command.Configuration)
            : command.Configuration.Clone();
        Source? updated = await sourceRepository.UpdateAsync(command.TenantId, command.Id, configuration, cancellationToken);
        return updated is null ? null : SourceDto.From(updated);
    }

    private static JsonElement PreserveWebhookCallbackId(JsonElement existing, JsonElement replacement)
    {
        if (!existing.TryGetProperty("callback_id", out JsonElement callbackId))
            throw new SourceValidationException("Webhook Source has no callback identity.");
        JsonObject copy = JsonNode.Parse(replacement.GetRawText())!.AsObject();
        copy["callback_id"] = callbackId.GetString();
        return JsonSerializer.SerializeToElement(copy);
    }
}
