using Integrios.Application.Abstractions;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.Bootstrap;

public sealed record BootstrapAdminKeyCommand(string PublicKey, string? Secret) : IRequest<BootstrapAdminKeyResult>;

public sealed record BootstrapAdminKeyResult(bool Created, string? GeneratedSecret);

public sealed class BootstrapAdminKeyCommandHandler(IAdminKeyRepository repository)
    : IRequestHandler<BootstrapAdminKeyCommand, BootstrapAdminKeyResult>
{
    public async Task<BootstrapAdminKeyResult> Handle(BootstrapAdminKeyCommand command, CancellationToken cancellationToken)
    {
        if (await repository.HasLiveGlobalKeyAsync(cancellationToken))
            return new BootstrapAdminKeyResult(Created: false, GeneratedSecret: null);

        string? generatedSecret = command.Secret is null ? AdminKeySecrets.Generate() : null;
        string secret = command.Secret ?? generatedSecret!;

        await repository.InsertAsync(new AdminKey
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            PublicKey = command.PublicKey,
            SecretHash = AdminKeySecrets.Hash(secret),
            Name = "Bootstrap Global Admin Key",
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        return new BootstrapAdminKeyResult(Created: true, GeneratedSecret: generatedSecret);
    }
}
