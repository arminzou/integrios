using Integrios.Application.Authoring.OperatorKeys;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Bootstrap;

public sealed record BootstrapOperatorKeyCommand(string PublicKey, string? Secret) : IRequest<BootstrapOperatorKeyResult>;

public sealed record BootstrapOperatorKeyResult(bool Created, string? GeneratedSecret);

internal sealed class BootstrapOperatorKeyCommandHandler(IOperatorKeyLifecycle operatorKeyLifecycle)
    : IRequestHandler<BootstrapOperatorKeyCommand, BootstrapOperatorKeyResult>
{
    public async Task<BootstrapOperatorKeyResult> Handle(BootstrapOperatorKeyCommand command, CancellationToken cancellationToken)
    {
        if (await operatorKeyLifecycle.HasLiveKeyAsync(cancellationToken))
            return new BootstrapOperatorKeyResult(Created: false, GeneratedSecret: null);

        // An empty/whitespace secret (e.g. an unset env var interpolated as "") must generate,
        // never be stored: SHA256("") would mint a key no auth header can ever present.
        string? generatedSecret = string.IsNullOrWhiteSpace(command.Secret) ? OperatorKeySecrets.Generate() : null;
        string secret = generatedSecret ?? command.Secret!;

        await operatorKeyLifecycle.InsertAsync(new OperatorKey
        {
            Id = Guid.NewGuid(),
            PublicKey = command.PublicKey,
            SecretHash = OperatorKeySecrets.Hash(secret),
            Name = "Bootstrap Operator Key",
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        return new BootstrapOperatorKeyResult(Created: true, GeneratedSecret: generatedSecret);
    }
}
