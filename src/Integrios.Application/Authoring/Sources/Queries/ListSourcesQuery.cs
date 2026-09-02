using MediatR;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Sources;

public sealed record ListSourcesQuery(Guid TenantId, SourceStatus? Status, SourceType? Type, string? AfterCursor, int Limit) : IRequest<SourceListDto>;

internal sealed class ListSourcesQueryHandler(ISourceRepository sourceRepository) : IRequestHandler<ListSourcesQuery, SourceListDto>
{
    public async Task<SourceListDto> Handle(ListSourcesQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await sourceRepository.ListByTenantAsync(query.TenantId, query.Status, query.Type, query.AfterCursor, query.Limit, cancellationToken);
        return new SourceListDto(items.Select(SourceListItemDto.From).ToList(), nextCursor);
    }
}
