using System.Security.Cryptography;
using Integrios.Application.OperatorKeys;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Bootstrap;

public sealed record RotateOperatorKeyCommand(string Secret) : IRequest<RotateOperatorKeyResult>;

public sealed record RotateOperatorKeyResult(string PublicKey);

internal sealed class RotateOperatorKeyCommandHandler(IOperatorKeyLifecycle operatorKeyLifecycle)
    : IRequestHandler<RotateOperatorKeyCommand, RotateOperatorKeyResult>
{
    public async Task<RotateOperatorKeyResult> Handle(RotateOperatorKeyCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Secret))
            throw new ArgumentException("A non-empty replacement OperatorKey secret is required.", nameof(command));

        string publicKey = "operator_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        await operatorKeyLifecycle.RotateAsync(new OperatorKey
        {
            Id = Guid.NewGuid(),
            PublicKey = publicKey,
            SecretHash = OperatorKeySecrets.Hash(command.Secret),
            Name = "Rotated Operator Key",
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        return new RotateOperatorKeyResult(publicKey);
    }
}
