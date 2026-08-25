using System.Text.Json;
using System.Text.Json.Nodes;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Topics;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record CreateSourceCommand(Guid TenantId, Guid ConnectionId, Guid TopicId, SourceType Type, JsonElement Configuration) : IRequest<SourceDto>;

internal sealed class CreateSourceCommandHandler(
    ISourceRepository sourceRepository,
    IConnectionRepository connectionRepository,
    IConnectionAuthoringLock authoringLock,
    IConnectorReader connectorReader,
    ITopicRepository topicRepository)
    : IRequestHandler<CreateSourceCommand, SourceDto>
{
    public async Task<SourceDto> Handle(CreateSourceCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync([command.ConnectionId], cancellationToken);
        Connection connection = await connectionRepository.GetByIdAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw new SourceValidationException("Source Connection must exist in the same Tenant.");
        Topic topic = await topicRepository.GetByIdAsync(command.TenantId, command.TopicId, cancellationToken)
            ?? throw new SourceValidationException("Source Topic must exist in the same Tenant.");
        if (topic.Status != OperationalStatus.Active)
            throw new SourceValidationException("Source Topic must be active.");
        Connector connector = await connectorReader.GetByIdAsync(connection.ConnectorId, cancellationToken)
            ?? throw new SourceValidationException("Source Connection references a Connector that does not exist.");
        SourceAuthoringValidator.Validate(command.Type, command.Configuration, connection, connector);
        JsonElement configuration = command.Type == SourceType.Webhook
            ? WithWebhookCallbackId(command.Configuration, Guid.NewGuid())
            : command.Configuration.Clone();

        var now = DateTimeOffset.UtcNow;
        var source = new Source
        {
            Id = Guid.NewGuid(), TenantId = command.TenantId, ConnectionId = command.ConnectionId, TopicId = command.TopicId,
            Type = command.Type, Configuration = configuration, Status = SourceStatus.Active,
            CreatedAt = now, UpdatedAt = now,
        };
        return SourceDto.From(await sourceRepository.CreateAsync(source, cancellationToken));
    }

    private static JsonElement WithWebhookCallbackId(JsonElement configuration, Guid callbackId)
    {
        JsonObject copy = JsonNode.Parse(configuration.GetRawText())!.AsObject();
        copy["callback_id"] = callbackId.ToString();
        return JsonSerializer.SerializeToElement(copy);
    }
}
