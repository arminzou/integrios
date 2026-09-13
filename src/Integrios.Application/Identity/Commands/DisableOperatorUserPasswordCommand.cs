using MediatR;

namespace Integrios.Application.Identity;

public sealed record DisableOperatorUserPasswordCommand(Guid UserId)
    : IRequest<PasswordCredentialMutationResult>;

internal sealed class DisableOperatorUserPasswordCommandHandler(IPasswordCredentialLifecycle lifecycle)
    : IRequestHandler<DisableOperatorUserPasswordCommand, PasswordCredentialMutationResult>
{
    public async Task<PasswordCredentialMutationResult> Handle(
        DisableOperatorUserPasswordCommand command,
        CancellationToken cancellationToken)
    {
        PasswordCredentialMutationStatus status = await lifecycle.DisableAsync(
            command.UserId, DateTimeOffset.UtcNow, cancellationToken);
        return new PasswordCredentialMutationResult(status, command.UserId);
    }
}
