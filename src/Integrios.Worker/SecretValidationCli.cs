using Integrios.Application.Secrets;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Worker;

public static class SecretValidationCli
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
        if (!TryParse(args, out ValidateSecretsCommand? command))
        {
            await error.WriteLineAsync(
                "Usage: secrets validate (--all | --tenant <slug> [--connection <id>])");
            return 2;
        }

        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            SecretValidationReport report = await mediator.Send(command!, cancellationToken);

            foreach (SecretValidationResult result in report.Results)
            {
                await output.WriteLineAsync(
                    $"{result.TenantSlug} / connection {result.ConnectionId} / {result.SecretReference}: "
                    + (result.Resolvable ? "resolvable" : "unresolvable"));
            }

            await output.WriteLineAsync(
                $"Validated {report.Results.Count} secret reference(s): "
                + (report.Succeeded ? "resolvable" : "one or more unresolvable"));
            return report.Succeeded ? 0 : 1;
        }
        catch (SecretValidationSelectionException ex)
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

    private static bool TryParse(string[] args, out ValidateSecretsCommand? command)
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
        Guid? connectionId = null;

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
                case "--connection" when connectionId is null && index + 1 < args.Length:
                    if (!Guid.TryParse(args[++index], out Guid parsedConnectionId))
                        return false;
                    connectionId = parsedConnectionId;
                    break;
                default:
                    return false;
            }
        }

        if (all == (tenantSlug is not null) || (connectionId is not null && tenantSlug is null))
            return false;

        command = new ValidateSecretsCommand(tenantSlug, connectionId, all);
        return true;
    }
}
