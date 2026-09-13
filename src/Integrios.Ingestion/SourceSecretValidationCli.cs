using Integrios.Application.Ingestion;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Ingestion;

/// Answers whether a Source's secret references resolve, before traffic depends on them. Ingestion
/// owns this because it holds the only mount that can resolve one: Admin authors the Source and has
/// no resolver, and the Worker sees destination secrets only.
public static class SourceSecretValidationCli
{
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0].Equals("secrets", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out ValidateSourceSecretsCommand? command))
        {
            await error.WriteLineAsync(
                "Usage: secrets validate (--all | --tenant <slug> [--source <id>])");
            return 2;
        }

        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            SourceSecretValidationReport report = await mediator.Send(command!, cancellationToken);

            foreach (SourceSecretValidationResult result in report.Results)
            {
                await output.WriteLineAsync(
                    $"{result.TenantSlug} / source {result.SourceId} / {result.SecretReference}: "
                    + (result.Resolvable ? "resolvable" : "unresolvable"));
            }

            await output.WriteLineAsync(
                $"Validated {report.Results.Count} secret reference(s): "
                + (report.Succeeded ? "resolvable" : "one or more unresolvable"));
            return report.Succeeded ? 0 : 1;
        }
        catch (SourceSecretValidationSelectionException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 2;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Secret validation could not start with the current configuration.");
            return 2;
        }
    }

    private static bool TryParse(string[] args, out ValidateSourceSecretsCommand? command)
    {
        command = null;
        if (args.Length < 3
            || !args[0].Equals("secrets", StringComparison.OrdinalIgnoreCase)
            || !args[1].Equals("validate", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool all = false;
        string? tenantSlug = null;
        Guid? sourceId = null;

        for (int index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--all" when !all:
                    all = true;
                    break;
                case "--tenant" when tenantSlug is null && index + 1 < args.Length:
                    tenantSlug = args[++index];
                    break;
                case "--source" when sourceId is null && index + 1 < args.Length:
                    if (!Guid.TryParse(args[++index], out Guid parsedSourceId))
                        return false;
                    sourceId = parsedSourceId;
                    break;
                default:
                    return false;
            }
        }

        if (all == (tenantSlug is not null) || (sourceId is not null && tenantSlug is null))
            return false;

        command = new ValidateSourceSecretsCommand(tenantSlug, sourceId, all);
        return true;
    }
}
