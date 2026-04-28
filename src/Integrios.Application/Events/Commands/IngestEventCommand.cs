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
        Guid? topicId = null;
        if (!string.IsNullOrWhiteSpace(command.Request.TopicName))
            topicId = await topicRepository.FindByNameAsync(command.TenantId, command.Request.TopicName, cancellationToken);

        return await eventRepository.IngestAsync(command.TenantId, command.Request, topicId, cancellationToken);
    }
}
