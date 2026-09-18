using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record ListSourcesQuery(Guid TenantId, EnablementStatus? Status, SourceType? Type, Guid? TopicId, string? AfterCursor, int Limit) : IRequest<SourceListDto>;

internal sealed class ListSourcesQueryHandler(ISourceQueries queries) : IRequestHandler<ListSourcesQuery, SourceListDto>
{
    public Task<SourceListDto> Handle(ListSourcesQuery query, CancellationToken cancellationToken) =>
        queries.ListAsync(query.TenantId, query.Status, query.Type, query.TopicId, query.AfterCursor, query.Limit, cancellationToken);
}
