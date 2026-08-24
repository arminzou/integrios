namespace Integrios.Application.Ingestion;

public interface ISourceTopicLookup
{
    Task<Guid?> FindActiveSourceTopicAsync(
        Guid tenantId,
        string topicName,
        Guid sourceConnectionId,
        CancellationToken cancellationToken);
}
