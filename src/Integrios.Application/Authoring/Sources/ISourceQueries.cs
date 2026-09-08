using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Sources;

public interface ISourceQueries
{
    Task<SourceListDto> ListAsync(Guid tenantId, SourceStatus? status, SourceType? type, Guid? topicId, string? afterCursor, int limit, CancellationToken cancellationToken);
}
