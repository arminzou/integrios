using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record GetSourceByIdQuery(Guid TenantId, Guid Id) : IRequest<SourceDto?>;

internal sealed class GetSourceByIdQueryHandler(ISourceRepository sourceRepository) : IRequestHandler<GetSourceByIdQuery, SourceDto?>
{
    public async Task<SourceDto?> Handle(GetSourceByIdQuery query, CancellationToken cancellationToken)
    {
        var source = await sourceRepository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return source is null ? null : SourceDto.From(source);
    }
}
