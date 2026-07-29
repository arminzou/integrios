using Integrios.Application.Abstractions;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Bootstrap;

public sealed record BootstrapBuiltinsCommand : IRequest<IReadOnlyList<Integration>>;

internal sealed class BootstrapBuiltinsCommandHandler(IIntegrationRepository repository)
    : IRequestHandler<BootstrapBuiltinsCommand, IReadOnlyList<Integration>>
{
    public async Task<IReadOnlyList<Integration>> Handle(BootstrapBuiltinsCommand command, CancellationToken cancellationToken)
    {
        var reconciled = new List<Integration>();
        foreach (BuiltinIntegration builtin in BuiltinCatalog.All)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var integration = new Integration
            {
                Id = builtin.Id,
                Key = builtin.Key,
                Name = builtin.Name,
                Direction = builtin.Direction,
                SupportedAuthSchemes = builtin.SupportedAuthSchemes,
                Status = OperationalStatus.Active,
                Description = builtin.Description,
                CreatedAt = now,
                UpdatedAt = now,
            };

            reconciled.Add(await repository.UpsertBuiltinAsync(integration, cancellationToken));
        }

        return reconciled;
    }
}
