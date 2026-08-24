using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record RevokeSourceCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class RevokeSourceCommandHandler(ISourceRepository sourceRepository) : IRequestHandler<RevokeSourceCommand, bool>
{
    public Task<bool> Handle(RevokeSourceCommand command, CancellationToken cancellationToken) =>
        sourceRepository.RevokeAsync(command.TenantId, command.Id, cancellationToken);
}
