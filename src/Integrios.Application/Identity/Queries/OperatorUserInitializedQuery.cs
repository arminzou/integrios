using MediatR;

namespace Integrios.Application.Identity;

public sealed record OperatorUserInitializedQuery : IRequest<bool>;

internal sealed class OperatorUserInitializedQueryHandler(IPasswordCredentialLifecycle lifecycle)
    : IRequestHandler<OperatorUserInitializedQuery, bool>
{
    public Task<bool> Handle(OperatorUserInitializedQuery query, CancellationToken cancellationToken) =>
        lifecycle.IsInitializedAsync(cancellationToken);
}
