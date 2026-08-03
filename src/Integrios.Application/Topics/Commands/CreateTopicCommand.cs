using MediatR;
using Integrios.Application.Connections;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Topics;

public sealed record CreateTopicCommand(
    Guid TenantId,
    string Name,
    string? Description,
    IReadOnlyList<Guid> SourceConnectionIds)
    : IRequest<TopicResponse>;

internal sealed class CreateTopicCommandHandler(
    ITopicRepository topicRepository,
    IConnectionRepository connectionRepository,
    IConnectionAuthoringLock authoringLock,
    IIntegrationCatalog integrationCatalog)
    : IRequestHandler<CreateTopicCommand, TopicResponse>
{
    public async Task<TopicResponse> Handle(CreateTopicCommand command, CancellationToken cancellationToken)
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
        return TopicResponse.From(topic);
    }

    private async Task ValidateSourceConnections(
        Guid tenantId,
        IReadOnlyList<Guid> connectionIds,
        CancellationToken cancellationToken)
    {
        foreach (Guid connectionId in connectionIds.Distinct())
        {
            Connection connection = await connectionRepository.GetByIdAsync(tenantId, connectionId, cancellationToken)
                ?? throw new TopicRequestValidationException(
                    "Every source Connection must exist in the same Tenant as the Topic.");
            Integration integration = await integrationCatalog.GetByIdAsync(connection.IntegrationId, cancellationToken)
                ?? throw new TopicRequestValidationException(
                    "A source Connection references an Integration that does not exist.");
            try
            {
                ConnectionRoleValidator.ValidateSource(connection, integration);
            }
            catch (ConnectionRequestValidationException exception)
            {
                throw new TopicRequestValidationException(exception.Message, exception);
            }
        }
    }
}
