using Integrios.Application.Connections;
using Integrios.Application.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Topics;

public sealed record CreateTopicCommand(
    Guid TenantId,
    string Name,
    string? Description,
    IReadOnlyList<Guid> SourceConnectionIds)
    : IRequest<TopicDto>;

internal sealed class CreateTopicCommandHandler(
    ITopicRepository topicRepository,
    IConnectionRepository connectionRepository,
    IConnectionAuthoringLock authoringLock,
    IConnectorCatalog connectorCatalog)
    : IRequestHandler<CreateTopicCommand, TopicDto>
{
    public async Task<TopicDto> Handle(CreateTopicCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            command.SourceConnectionIds,
            cancellationToken);
        await ValidateSourceConnections(command.TenantId, command.SourceConnectionIds, cancellationToken);
        var topic = await topicRepository.CreateAsync(
            command.TenantId,
            command.Name,
            command.Description,
            command.SourceConnectionIds,
            cancellationToken);
        return TopicDto.From(topic);
    }

    private async Task ValidateSourceConnections(
        Guid tenantId,
        IReadOnlyList<Guid> connectionIds,
        CancellationToken cancellationToken)
    {
        foreach (Guid connectionId in connectionIds.Distinct())
        {
            Connection connection = await connectionRepository.GetByIdAsync(tenantId, connectionId, cancellationToken)
                ?? throw new TopicValidationException(
                    "Every source Connection must exist in the same Tenant as the Topic.");
            Connector connector = await connectorCatalog.GetByIdAsync(connection.ConnectorId, cancellationToken)
                ?? throw new TopicValidationException(
                    "A source Connection references a Connector that does not exist.");
            try
            {
                ConnectionUseValidator.ValidateSourceAuthoring(connection, connector);
            }
            catch (ConnectionValidationException exception)
            {
                throw new TopicValidationException(exception.Message, exception);
            }
        }
    }
}
