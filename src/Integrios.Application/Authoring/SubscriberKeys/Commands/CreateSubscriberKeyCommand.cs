using System.Security.Cryptography;
using System.Text;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;
using Integrios.Application.Authoring.Destinations;
using Integrios.Application.Authoring.Connectors;

namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record CreateSubscriberKeyCommand(
    Guid TenantId, Guid DestinationId,
    string? Name,
    string? Description
) : IRequest<CreateSubscriberKeyResult>;

internal sealed class CreateSubscriberKeyCommandHandler(ISubscriberKeyRepository repository, IDestinationRepository destinations, IConnectorReader connectors)
    : IRequestHandler<CreateSubscriberKeyCommand, CreateSubscriberKeyResult>
{
    public async Task<CreateSubscriberKeyResult> Handle(CreateSubscriberKeyCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new SubscriberKeyValidationException("Name is required.", "name");

        Destination destination = await destinations.GetByIdAsync(command.TenantId, command.DestinationId, cancellationToken)
            ?? throw new SubscriberKeyValidationException("The specified Destination does not exist.", "destination_id");
        Connector? connector = await connectors.GetByIdAsync(destination.ConnectorId, cancellationToken);
        if (connector is null || Connector.TransportFor(connector.Key) != DeliveryTransport.Pull)
            throw new SubscriberKeyValidationException("Subscriber keys require a pull Destination.", "destination_id");

        var rawKey = "intg_sub_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var keyPrefix = rawKey[..16];
        var keyHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

        var subscriberKey = new SubscriberKey
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            DestinationId = command.DestinationId,
            Name = command.Name,
            KeyPrefix = keyPrefix,
            KeyHash = keyHash,
            Description = command.Description,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        SubscriberKey created = await repository.CreateAsync(subscriberKey, cancellationToken);
        return new CreateSubscriberKeyResult
        {
            SubscriberKey = SubscriberKeyDto.From(created),
            Token = rawKey,
        };
    }
}
