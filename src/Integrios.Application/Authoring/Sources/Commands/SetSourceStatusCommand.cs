using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

// Enabling opens intake for the Source's declared Event types; disabling fences new acceptance.
// Either way the Source keeps its identity, callback, and declarations, so the change is reversible.
public sealed record SetSourceStatusCommand(Guid TenantId, Guid Id, EnablementStatus Status) : IRequest<SourceDto?>;

internal sealed class SetSourceStatusCommandHandler(ISourceRepository sourceRepository)
    : IRequestHandler<SetSourceStatusCommand, SourceDto?>
{
    public async Task<SourceDto?> Handle(SetSourceStatusCommand command, CancellationToken cancellationToken)
    {
        if (!await sourceRepository.SetStatusAsync(command.TenantId, command.Id, command.Status, cancellationToken))
            return null;
        Source? source = await sourceRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        return source is null ? null : SourceDto.From(source);
    }
}
