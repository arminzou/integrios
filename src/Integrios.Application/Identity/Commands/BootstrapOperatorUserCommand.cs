using MediatR;

namespace Integrios.Application.Identity;

public sealed record BootstrapOperatorUserCommand(
    string? DisplayName,
    string? Email,
    string? PasswordHash) : IRequest<bool>;

internal sealed class BootstrapOperatorUserCommandHandler(IPasswordCredentialLifecycle lifecycle)
    : IRequestHandler<BootstrapOperatorUserCommand, bool>
{
    public async Task<bool> Handle(BootstrapOperatorUserCommand command, CancellationToken cancellationToken)
    {
        if (await lifecycle.IsInitializedAsync(cancellationToken))
            return false;

        var (user, credential) = CreateOperatorUserCommandHandler.CreateEntities(new CreateOperatorUserCommand(
            command.DisplayName ?? string.Empty,
            command.Email ?? string.Empty,
            command.PasswordHash ?? string.Empty));
        return await lifecycle.CreateFirstAsync(user, credential, cancellationToken);
    }
}
