using System.Security.Cryptography;
using Integrios.Application.Abstractions;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.Bootstrap;

// Not wired to a bootstrap CLI verb or HTTP endpoint yet: the bootstrap paradox only blocks
// *creating* the first global AdminKey. Rotating an existing one is future Admin-API scope;
// this exists now so the schema's rotate story (fresh public_key, revoke prior row, no UNIQUE
// collision) is proven and covered directly.
public sealed record RotateAdminKeyCommand(string? Secret) : IRequest<RotateAdminKeyResult>;

public sealed record RotateAdminKeyResult(string PublicKey, string? GeneratedSecret);

public sealed class RotateAdminKeyCommandHandler(IAdminKeyRepository repository)
    : IRequestHandler<RotateAdminKeyCommand, RotateAdminKeyResult>
{
    public async Task<RotateAdminKeyResult> Handle(RotateAdminKeyCommand command, CancellationToken cancellationToken)
    {
        string publicKey = "admin_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string? generatedSecret = string.IsNullOrWhiteSpace(command.Secret) ? AdminKeySecrets.Generate() : null;
        string secret = generatedSecret ?? command.Secret!;

        await repository.RotateGlobalAsync(new AdminKey
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            PublicKey = publicKey,
            SecretHash = AdminKeySecrets.Hash(secret),
            Name = "Rotated Global Admin Key",
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        return new RotateAdminKeyResult(publicKey, generatedSecret);
    }
}
