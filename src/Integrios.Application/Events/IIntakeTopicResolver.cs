namespace Integrios.Application.Events;

public interface IIntakeTopicResolver
{
    Task<Guid?> FindActiveSourceTopicAsync(
        Guid tenantId,
        string topicName,
        Guid sourceConnectionId,
        CancellationToken cancellationToken = default);
}
