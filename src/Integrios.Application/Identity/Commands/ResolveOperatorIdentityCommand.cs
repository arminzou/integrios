using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Identity;

public sealed record ResolveOperatorIdentityCommand(
    string Issuer,
    string Subject,
    OperatorIdentityClaims Claims) : IRequest<OperatorUserDto>;

public sealed record OperatorUserDto(Guid UserId, string DisplayName, string? Email)
{
    public static OperatorUserDto From(User user) => new(user.Id, user.DisplayName, user.Email);
}

internal sealed class ResolveOperatorIdentityCommandHandler(IOperatorIdentityStore store)
    : IRequestHandler<ResolveOperatorIdentityCommand, OperatorUserDto>
{
    public async Task<OperatorUserDto> Handle(
        ResolveOperatorIdentityCommand command,
        CancellationToken cancellationToken)
    {
        User user = await store.ResolveAsync(
            command.Issuer, command.Subject, command.Claims, cancellationToken);
        return OperatorUserDto.From(user);
    }
}
