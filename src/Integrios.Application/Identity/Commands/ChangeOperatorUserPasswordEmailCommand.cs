using MediatR;

namespace Integrios.Application.Identity;

public sealed record ChangeOperatorUserPasswordEmailCommand(
    Guid UserId,
    string Email) : IRequest<PasswordCredentialMutationResult>;

internal sealed class ChangeOperatorUserPasswordEmailCommandHandler(IPasswordCredentialLifecycle lifecycle)
    : IRequestHandler<ChangeOperatorUserPasswordEmailCommand, PasswordCredentialMutationResult>
{
    public async Task<PasswordCredentialMutationResult> Handle(
        ChangeOperatorUserPasswordEmailCommand command,
        CancellationToken cancellationToken)
    {
        if (!PasswordCredentialRules.TryNormalizeEmail(command.Email, out string email, out string normalizedEmail))
            throw new ArgumentException("A valid sign-in email is required.", nameof(command));

        PasswordCredentialMutationStatus status = await lifecycle.ChangeEmailAsync(
            command.UserId,
            email,
            normalizedEmail,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return new PasswordCredentialMutationResult(status, command.UserId);
    }
}
