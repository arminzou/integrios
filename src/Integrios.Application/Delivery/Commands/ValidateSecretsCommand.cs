using Integrios.Application.Secrets;
using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Delivery;

public sealed record ValidateSecretsCommand(
    string? TenantSlug,
    Guid? DestinationId,
    bool All) : IRequest<SecretValidationReport>;

public sealed record SecretValidationResult(
    string TenantSlug,
    Guid DestinationId,
    string SecretReference,
    bool Resolvable);

public sealed record SecretValidationReport(IReadOnlyList<SecretValidationResult> Results)
{
    public bool Succeeded => Results.All(result => result.Resolvable);
}

public sealed class SecretValidationSelectionException(string message) : Exception(message);

internal sealed class ValidateSecretsCommandHandler(
    ISecretValidationReader reader,
    IDestinationAuthenticationSecretResolver secretResolver) : IRequestHandler<ValidateSecretsCommand, SecretValidationReport>
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
            IReadOnlyList<Destination> destinations = await SelectDestinationsAsync(
                tenant,
                command.DestinationId,
                cancellationToken);

            foreach (Destination destination in destinations)
            {
                foreach (string reference in SecretReferences(destination))
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
                        destination.Id,
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
            || (command.DestinationId is not null && command.TenantSlug is null))
        {
            throw new SecretValidationSelectionException(
                "Select --all or --tenant <slug>; --destination requires --tenant.");
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
            Tenant? tenant = await reader.FindTenantBySlugAsync(command.TenantSlug, cancellationToken);
            if (tenant is null)
                throw new SecretValidationSelectionException("The selected Tenant does not exist.");
            if (tenant.Status != OperationalStatus.Active)
                throw new SecretValidationSelectionException("The selected Tenant is not active.");
            return [tenant];
        }

        return await reader.ListActiveTenantsAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Destination>> SelectDestinationsAsync(
        Tenant tenant,
        Guid? destinationId,
        CancellationToken cancellationToken)
    {
        if (destinationId is not null)
        {
            Destination? destination = await reader.FindDestinationAsync(
                tenant.Id,
                destinationId.Value,
                cancellationToken);
            return destination is null
                ? throw new SecretValidationSelectionException("The selected Destination does not exist for this Tenant.")
                : [destination];
        }

        return await reader.ListActiveDestinationsAsync(tenant.Id, cancellationToken);
    }

    private static IEnumerable<string> SecretReferences(Destination destination)
    {
        JsonElement references = destination.Authentication?.SecretRefs ?? default;
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
