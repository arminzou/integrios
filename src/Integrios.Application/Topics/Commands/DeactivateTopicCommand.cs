using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Topics;

public sealed record DeactivateTopicCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class DeactivateTopicCommandHandler(ITopicRepository topicRepository)
    : IRequestHandler<DeactivateTopicCommand, bool>
{
    public Task<bool> Handle(DeactivateTopicCommand command, CancellationToken cancellationToken) =>
        topicRepository.DeactivateAsync(command.TenantId, command.Id, cancellationToken);
}
