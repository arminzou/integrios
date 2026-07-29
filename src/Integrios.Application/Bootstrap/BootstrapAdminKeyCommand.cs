using Integrios.Application.Abstractions;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.Bootstrap;

public sealed record BootstrapAdminKeyCommand(string PublicKey, string? Secret) : IRequest<BootstrapAdminKeyResult>;

public sealed record BootstrapAdminKeyResult(bool Created, string? GeneratedSecret);

internal sealed class BootstrapAdminKeyCommandHandler(IAdminKeyRepository repository)
    : IRequestHandler<BootstrapAdminKeyCommand, BootstrapAdminKeyResult>
{
    public async Task<BootstrapAdminKeyResult> Handle(BootstrapAdminKeyCommand command, CancellationToken cancellationToken)
    {
        if (await repository.HasLiveKeyAsync(cancellationToken))
            return new BootstrapAdminKeyResult(Created: false, GeneratedSecret: null);

        // An empty/whitespace secret (e.g. an unset env var interpolated as "") must generate,
        // never be stored: SHA256("") would mint a key no auth header can ever present.
        string? generatedSecret = string.IsNullOrWhiteSpace(command.Secret) ? AdminKeySecrets.Generate() : null;
        string secret = generatedSecret ?? command.Secret!;

        await repository.InsertAsync(new AdminKey
        {
            Id = Guid.NewGuid(),
            PublicKey = command.PublicKey,
            SecretHash = AdminKeySecrets.Hash(secret),
            Name = "Bootstrap Operator Admin Key",
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        return new BootstrapAdminKeyResult(Created: true, GeneratedSecret: generatedSecret);
    }
}
