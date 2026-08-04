using System.Security.Cryptography;
using Integrios.Application.AdminKeys;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.Bootstrap;

public sealed record RotateAdminKeyCommand(string Secret) : IRequest<RotateAdminKeyResult>;

public sealed record RotateAdminKeyResult(string PublicKey);

internal sealed class RotateAdminKeyCommandHandler(IAdminKeyLifecycle adminKeyLifecycle)
    : IRequestHandler<RotateAdminKeyCommand, RotateAdminKeyResult>
{
    public async Task<RotateAdminKeyResult> Handle(RotateAdminKeyCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Secret))
            throw new ArgumentException("A non-empty replacement AdminKey secret is required.", nameof(command));

        string publicKey = "admin_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        await adminKeyLifecycle.RotateAsync(new AdminKey
        {
            Id = Guid.NewGuid(),
            PublicKey = publicKey,
            SecretHash = AdminKeySecrets.Hash(command.Secret),
            Name = "Rotated Operator Admin Key",
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        return new RotateAdminKeyResult(publicKey);
    }
}
