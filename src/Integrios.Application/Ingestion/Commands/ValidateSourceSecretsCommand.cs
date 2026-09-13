using System.Text.Json;
using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Ingestion;

public sealed record ValidateSourceSecretsCommand(
    string? TenantSlug,
    Guid? SourceId,
    bool All) : IRequest<SourceSecretValidationReport>;

public sealed record SourceSecretValidationResult(
    string TenantSlug,
    Guid SourceId,
    string SecretReference,
    bool Resolvable);

public sealed record SourceSecretValidationReport(IReadOnlyList<SourceSecretValidationResult> Results)
{
    public bool Succeeded => Results.All(result => result.Resolvable);
}

public sealed class SourceSecretValidationSelectionException(string message) : Exception(message);

internal sealed class ValidateSourceSecretsCommandHandler(
    ISourceSecretValidationReader reader,
    ISourceVerificationSecretResolver secretResolver)
    : IRequestHandler<ValidateSourceSecretsCommand, SourceSecretValidationReport>
{
    public async Task<SourceSecretValidationReport> Handle(
        ValidateSourceSecretsCommand command,
        CancellationToken cancellationToken)
    {
        ValidateSelection(command);

        IReadOnlyList<Tenant> tenants = await SelectTenantsAsync(command, cancellationToken);
        List<SourceSecretValidationResult> results = [];

        foreach (Tenant tenant in tenants)
        {
            IReadOnlyList<Source> sources = await SelectSourcesAsync(tenant, command.SourceId, cancellationToken);

            foreach (Source source in sources)
            {
                foreach (string reference in SecretReferences(source))
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

                    results.Add(new(tenant.Slug, source.Id, reference, resolvable));
                }
            }
        }

        return new SourceSecretValidationReport(results);
    }

    private static void ValidateSelection(ValidateSourceSecretsCommand command)
    {
        if (command.All == (command.TenantSlug is not null)
            || (command.SourceId is not null && command.TenantSlug is null))
        {
            throw new SourceSecretValidationSelectionException(
                "Select --all or --tenant <slug>; --source requires --tenant.");
        }

        if (command.TenantSlug is not null && !TenantSlug.IsValid(command.TenantSlug))
            throw new SourceSecretValidationSelectionException("The selected Tenant slug is invalid.");
    }

    private async Task<IReadOnlyList<Tenant>> SelectTenantsAsync(
        ValidateSourceSecretsCommand command,
        CancellationToken cancellationToken)
    {
        if (command.TenantSlug is null)
            return await reader.ListActiveTenantsAsync(cancellationToken);

        Tenant tenant = await reader.FindTenantBySlugAsync(command.TenantSlug, cancellationToken)
            ?? throw new SourceSecretValidationSelectionException("The selected Tenant does not exist.");
        if (tenant.Status != OperationalStatus.Active)
            throw new SourceSecretValidationSelectionException("The selected Tenant is not active.");
        return [tenant];
    }

    private async Task<IReadOnlyList<Source>> SelectSourcesAsync(
        Tenant tenant,
        Guid? sourceId,
        CancellationToken cancellationToken)
    {
        if (sourceId is null)
            return await reader.ListActiveSourcesAsync(tenant.Id, cancellationToken);

        Source? source = await reader.FindSourceAsync(tenant.Id, sourceId.Value, cancellationToken);
        return source is null
            ? throw new SourceSecretValidationSelectionException("The selected Source does not exist for this Tenant.")
            : [source];
    }

    /// A Source carries its secrets in two shapes: webhook verification holds a {field: reference}
    /// object, while a queue holds one bare reference inside its transport configuration.
    private static IEnumerable<string> SecretReferences(Source source)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        if (source.Verification?.SecretRefs is { ValueKind: JsonValueKind.Object } verificationRefs)
        {
            foreach (JsonProperty property in verificationRefs.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } reference
                    && seen.Add(reference))
                {
                    yield return reference;
                }
            }
        }

        if (source.Type == SourceType.Queue
            && source.Configuration.ValueKind == JsonValueKind.Object
            && source.Configuration.TryGetProperty("authentication", out JsonElement authentication)
            && authentication.ValueKind == JsonValueKind.Object
            && authentication.TryGetProperty("secret_ref", out JsonElement secretRef)
            && secretRef.ValueKind == JsonValueKind.String
            && secretRef.GetString() is { Length: > 0 } queueReference
            && seen.Add(queueReference))
        {
            yield return queueReference;
        }
    }
}
