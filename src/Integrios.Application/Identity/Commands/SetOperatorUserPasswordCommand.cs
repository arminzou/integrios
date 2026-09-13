using MediatR;

namespace Integrios.Application.Identity;

public sealed record SetOperatorUserPasswordCommand(
    Guid UserId,
    string? Email,
    string PasswordHash) : IRequest<PasswordCredentialMutationResult>;

internal sealed class SetOperatorUserPasswordCommandHandler(IPasswordCredentialLifecycle lifecycle)
    : IRequestHandler<SetOperatorUserPasswordCommand, PasswordCredentialMutationResult>
{
    public async Task<PasswordCredentialMutationResult> Handle(
        SetOperatorUserPasswordCommand command,
        CancellationToken cancellationToken)
    {
        if (!PasswordCredentialRules.IsValidPasswordHash(command.PasswordHash))
            throw new ArgumentException("A valid password hash is required.", nameof(command));

        string? email = null;
        string? normalizedEmail = null;
        if (command.Email is not null
            && !PasswordCredentialRules.TryNormalizeEmail(command.Email, out email, out normalizedEmail))
        {
            throw new ArgumentException("A valid sign-in email is required.", nameof(command));
        }

        PasswordCredentialMutationStatus status = await lifecycle.SetPasswordAsync(
            command.UserId,
            email,
            normalizedEmail,
            command.PasswordHash,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return new PasswordCredentialMutationResult(status, command.UserId);
    }
}
