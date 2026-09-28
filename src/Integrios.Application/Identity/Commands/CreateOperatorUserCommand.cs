using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Identity;

public sealed record CreateOperatorUserCommand(
    string DisplayName,
    string Email,
    string PasswordHash) : IRequest<PasswordCredentialMutationResult>;

internal sealed class CreateOperatorUserCommandHandler(IPasswordCredentialLifecycle lifecycle)
    : IRequestHandler<CreateOperatorUserCommand, PasswordCredentialMutationResult>
{
    public async Task<PasswordCredentialMutationResult> Handle(
        CreateOperatorUserCommand command,
        CancellationToken cancellationToken)
    {
        string displayName = command.DisplayName.Trim();
        if (displayName.Length == 0)
            throw new ArgumentException("Display name is required.", nameof(command));
        if (!PasswordCredentialRules.TryNormalizeEmail(command.Email, out string email, out string normalizedEmail))
            throw new ArgumentException("A valid sign-in email is required.", nameof(command));
        if (!PasswordCredentialRules.IsValidPasswordHash(command.PasswordHash))
            throw new ArgumentException("A valid password hash is required.", nameof(command));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            Email = null,
            CreatedAt = now,
            LastSignedInAt = null,
        };
        var credential = new PasswordCredential
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = command.PasswordHash,
            SessionRevision = 1,
            CreatedAt = now,
            UpdatedAt = now,
            DisabledAt = null,
        };

        PasswordCredentialMutationStatus status = await lifecycle.CreateAsync(
            user, credential, cancellationToken);
        return new PasswordCredentialMutationResult(status, user.Id);
    }
}
