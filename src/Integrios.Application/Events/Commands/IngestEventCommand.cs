using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Events;

public sealed record IngestEventCommand(Guid TenantId, IngestEventRequest Request)
    : IRequest<IngestEventResponse>;

internal sealed class IngestEventCommandHandler(
    IEventRepository eventRepository,
    ITopicRepository topicRepository)
    : IRequestHandler<IngestEventCommand, IngestEventResponse>
{
    public async Task<IngestEventResponse> Handle(IngestEventCommand command, CancellationToken cancellationToken)
    {
        var topicId = await topicRepository.FindByNameAsync(command.TenantId, command.Request.TopicName, cancellationToken)
            ?? throw new InvalidOperationException($"topic '{command.Request.TopicName}' does not exist for this tenant");

        return await eventRepository.IngestAsync(command.TenantId, command.Request, topicId, cancellationToken);
    }
}
