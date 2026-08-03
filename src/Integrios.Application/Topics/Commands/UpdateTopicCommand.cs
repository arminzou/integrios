using MediatR;
using Integrios.Application.Connections;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;
using Integrios.Domain.Common;

namespace Integrios.Application.Topics;

public sealed record UpdateTopicCommand(
    Guid TenantId,
    Guid Id,
    string? Name,
    string? Description,
    IReadOnlyList<Guid>? SourceConnectionIds)
    : IRequest<TopicResponse?>;

internal sealed class UpdateTopicCommandHandler(
    ITopicRepository topicRepository,
    IConnectionRepository connectionRepository,
    IConnectionAuthoringLock authoringLock,
    IIntegrationCatalog integrationCatalog)
    : IRequestHandler<UpdateTopicCommand, TopicResponse?>
{
    public async Task<TopicResponse?> Handle(UpdateTopicCommand command, CancellationToken cancellationToken)
    {
        var existing = await topicRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (existing is null)
            return null;

        await using IAsyncDisposable? lease = command.SourceConnectionIds is null
            ? null
            : await authoringLock.AcquireAsync(command.SourceConnectionIds, cancellationToken);
        if (command.SourceConnectionIds is not null)
            await ValidateSourceConnections(command.TenantId, command.SourceConnectionIds, cancellationToken);

        var topic = await topicRepository.UpdateAsync(
            command.TenantId,
            command.Id,
            command.Name,
            command.Description,
            command.SourceConnectionIds,
            cancellationToken);

        return topic is null ? null : TopicResponse.From(topic);
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
                ConnectionUseValidator.ValidateSourceAuthoring(connection, integration);
            }
            catch (ConnectionRequestValidationException exception)
            {
                throw new TopicRequestValidationException(exception.Message, exception);
            }
        }
    }
}
