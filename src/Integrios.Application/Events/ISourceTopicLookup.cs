namespace Integrios.Application.Events;

public interface ISourceTopicLookup
{
    Task<Guid?> FindActiveSourceTopicAsync(
        Guid tenantId,
        string topicName,
        Guid sourceConnectionId,
        CancellationToken cancellationToken);
}
