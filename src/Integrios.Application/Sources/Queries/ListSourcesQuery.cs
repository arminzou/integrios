using MediatR;

namespace Integrios.Application.Sources;

public sealed record ListSourcesQuery(Guid TenantId, string? AfterCursor, int Limit) : IRequest<SourceListDto>;

internal sealed class ListSourcesQueryHandler(ISourceRepository sourceRepository) : IRequestHandler<ListSourcesQuery, SourceListDto>
{
    public async Task<SourceListDto> Handle(ListSourcesQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await sourceRepository.ListByTenantAsync(query.TenantId, query.AfterCursor, query.Limit, cancellationToken);
        return new SourceListDto(items.Select(SourceDto.From).ToList(), nextCursor);
    }
}
