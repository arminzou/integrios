using System.Text.Json;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.Secrets;

public sealed record ValidateSecretsCommand(
    string? TenantSlug,
    Guid? ConnectionId,
    bool All) : IRequest<SecretValidationReport>;

public sealed record SecretValidationResult(
    string TenantSlug,
    Guid ConnectionId,
    string SecretReference,
    bool Resolvable);

public sealed record SecretValidationReport(IReadOnlyList<SecretValidationResult> Results)
{
    public bool Succeeded => Results.All(result => result.Resolvable);
}

public sealed class SecretValidationSelectionException(string message) : Exception(message);

internal sealed class ValidateSecretsCommandHandler(
    ISecretValidationCatalog catalog,
    ISecretResolver secretResolver) : IRequestHandler<ValidateSecretsCommand, SecretValidationReport>
{
    public async Task<SecretValidationReport> Handle(
        ValidateSecretsCommand command,
        CancellationToken cancellationToken)
    {
        ValidateSelection(command);

        IReadOnlyList<Tenant> tenants = await SelectTenantsAsync(command, cancellationToken);
        List<SecretValidationResult> results = [];

        foreach (Tenant tenant in tenants)
        {
            IReadOnlyList<Connection> connections = await SelectConnectionsAsync(
                tenant,
                command.ConnectionId,
                cancellationToken);

            foreach (Connection connection in connections)
            {
                foreach (string reference in SecretReferences(connection))
                {
                    bool resolvable;
                    try
                    {
                        _ = await secretResolver.ResolveAsync(
                            new TenantSecretScope(tenant.Id, tenant.Slug),
                            reference,
                            cancellationToken);
                        resolvable = true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        resolvable = false;
                    }

                    results.Add(new(
                        tenant.Slug,
                        connection.Id,
                        reference,
                        resolvable));
                }
            }
        }

        return new SecretValidationReport(results);
    }

    private static void ValidateSelection(ValidateSecretsCommand command)
    {
        if (command.All == (command.TenantSlug is not null)
            || (command.ConnectionId is not null && command.TenantSlug is null))
        {
            throw new SecretValidationSelectionException(
                "Select --all or --tenant <slug>; --connection requires --tenant.");
        }

        if (command.TenantSlug is not null && !TenantSlug.IsValid(command.TenantSlug))
            throw new SecretValidationSelectionException("The selected Tenant slug is invalid.");
    }

    private async Task<IReadOnlyList<Tenant>> SelectTenantsAsync(
        ValidateSecretsCommand command,
        CancellationToken cancellationToken)
    {
        if (command.TenantSlug is not null)
        {
            Tenant? tenant = await catalog.FindTenantBySlugAsync(command.TenantSlug, cancellationToken);
            if (tenant is null)
                throw new SecretValidationSelectionException("The selected Tenant does not exist.");
            if (tenant.Status != OperationalStatus.Active)
                throw new SecretValidationSelectionException("The selected Tenant is not active.");
            return [tenant];
        }

        return await catalog.ListActiveTenantsAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Connection>> SelectConnectionsAsync(
        Tenant tenant,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        if (connectionId is not null)
        {
            Connection? connection = await catalog.FindConnectionAsync(
                tenant.Id,
                connectionId.Value,
                cancellationToken);
            return connection is null
                ? throw new SecretValidationSelectionException("The selected Connection does not exist for this Tenant.")
                : [connection];
        }

        return await catalog.ListActiveConnectionsAsync(tenant.Id, cancellationToken);
    }

    private static IEnumerable<string> SecretReferences(Connection connection)
    {
        JsonElement references = connection.Auth?.SecretRefs ?? default;
        if (references.ValueKind != JsonValueKind.Object)
            yield break;

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in references.EnumerateObject())
        {
            string? reference = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
            if (reference is not null && seen.Add(reference))
                yield return reference;
        }
    }
}
