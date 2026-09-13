using System.Text;
using Integrios.Application;
using Integrios.Application.Identity;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integrios.Admin.OperatorUsers;

public static class OperatorUserCli
{
    private static readonly PasswordHasher<string> PasswordHasher = new();
    private static readonly IOperatorUserConsole SystemConsole = new OperatorUserConsole();

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["operator-user", "list"])
            return await ListAsync();

        if (args.Length < 2)
            return Usage();

        return args[1] switch
        {
            "create" => await CreateAsync(args),
            "set-password" => await SetPasswordAsync(args),
            "change-email" => await ChangeEmailAsync(args),
            "disable-password" => await DisablePasswordAsync(args),
            _ => Usage(),
        };
    }

    private static async Task<int> ListAsync()
    {
        IReadOnlyList<OperatorUserCredentialDto> users = await SendAsync(new ListOperatorUsersQuery());
        Console.WriteLine("USER_ID\tDISPLAY_NAME\tDESCRIPTIVE_EMAIL\tPASSWORD_EMAIL\tPASSWORD_STATUS");
        foreach (OperatorUserCredentialDto user in users)
        {
            string passwordStatus = user.PasswordCredentialId is null
                ? "not_configured"
                : user.PasswordEnabled ? "enabled" : "disabled";
            Console.WriteLine(
                $"{user.UserId}\t{user.DisplayName}\t{user.DescriptiveEmail ?? "-"}\t{user.PasswordEmail ?? "-"}\t{passwordStatus}");
        }

        return 0;
    }

    private static async Task<int> CreateAsync(string[] args)
    {
        if (!TryReadOptions(args, ["--display-name", "--email"], out Dictionary<string, string> options)
            || !options.TryGetValue("--display-name", out string? displayName)
            || !options.TryGetValue("--email", out string? email))
        {
            return Usage();
        }

        if (!TryReadConfirmedPassword(SystemConsole, out string? password))
            return 1;

        PasswordCredentialMutationResult result;
        try
        {
            result = await SendAsync(new CreateOperatorUserCommand(
                displayName,
                email,
                PasswordHasher.HashPassword(string.Empty, password)));
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"operator-user create: {exception.Message}");
            return 2;
        }

        return WriteResult("create", result);
    }

    private static async Task<int> SetPasswordAsync(string[] args)
    {
        if (!TryReadOptions(args, ["--user-id", "--email"], out Dictionary<string, string> options)
            || !options.TryGetValue("--user-id", out string? userIdValue)
            || !Guid.TryParse(userIdValue, out Guid userId))
        {
            return Usage();
        }

        options.TryGetValue("--email", out string? email);
        if (!TryReadConfirmedPassword(SystemConsole, out string? password))
            return 1;

        PasswordCredentialMutationResult result;
        try
        {
            result = await SendAsync(new SetOperatorUserPasswordCommand(
                userId,
                email,
                PasswordHasher.HashPassword(string.Empty, password)));
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"operator-user set-password: {exception.Message}");
            return 2;
        }

        return WriteResult("set-password", result);
    }

    private static async Task<int> ChangeEmailAsync(string[] args)
    {
        if (!TryReadOptions(args, ["--user-id", "--email"], out Dictionary<string, string> options)
            || !options.TryGetValue("--user-id", out string? userIdValue)
            || !Guid.TryParse(userIdValue, out Guid userId)
            || !options.TryGetValue("--email", out string? email))
        {
            return Usage();
        }

        PasswordCredentialMutationResult result;
        try
        {
            result = await SendAsync(new ChangeOperatorUserPasswordEmailCommand(userId, email));
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"operator-user change-email: {exception.Message}");
            return 2;
        }

        return WriteResult("change-email", result);
    }

    private static async Task<int> DisablePasswordAsync(string[] args)
    {
        if (!TryReadOptions(args, ["--user-id"], out Dictionary<string, string> options)
            || !options.TryGetValue("--user-id", out string? userIdValue)
            || !Guid.TryParse(userIdValue, out Guid userId))
        {
            return Usage();
        }

        PasswordCredentialMutationResult result = await SendAsync(
            new DisableOperatorUserPasswordCommand(userId));
        return WriteResult("disable-password", result);
    }

    internal static bool TryReadOptions(
        string[] args,
        string[] allowed,
        out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 2; index < args.Length; index += 2)
        {
            string name = args[index];
            if (index + 1 >= args.Length
                || !allowed.Contains(name, StringComparer.Ordinal)
                || !options.TryAdd(name, args[index + 1]))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryReadConfirmedPassword(
        IOperatorUserConsole console,
        out string password)
    {
        password = string.Empty;
        if (!console.IsInteractive)
        {
            console.WriteError("operator-user: password entry requires an interactive terminal.");
            return false;
        }

        try
        {
            string first = ReadMasked(console, "Password: ");

            if (!PasswordCredentialRules.IsValidPassword(first))
            {
                console.WriteError(
                    $"operator-user: password must contain {PasswordCredentialRules.MinimumPasswordLength} through {PasswordCredentialRules.MaximumPasswordLength} Unicode characters.");
                return false;
            }

            string second = ReadMasked(console, "Confirm password: ");

            if (!first.Equals(second, StringComparison.Ordinal))
            {
                console.WriteError("operator-user: password confirmation does not match.");
                return false;
            }

            password = first;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            console.WriteError("operator-user: password entry requires an interactive terminal.");
            return false;
        }
    }

    private static string ReadMasked(IOperatorUserConsole console, string prompt)
    {
        console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = console.ReadKey();
            if (key.Key == ConsoleKey.Enter)
            {
                console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                    console.Write("\b \b");
                }

                continue;
            }

            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
                continue;

            value.Append(key.KeyChar);
            console.Write("*");
        }
    }

    private static async Task<T> SendAsync<T>(IRequest<T> request)
    {
        HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services.AddAdminApplicationServices();
        hostBuilder.Services.AddAdminInfrastructureServices(hostBuilder.Configuration);

        using IHost host = hostBuilder.Build();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private static int WriteResult(string operation, PasswordCredentialMutationResult result)
    {
        if (result.Status == PasswordCredentialMutationStatus.Succeeded)
        {
            Console.WriteLine($"operator-user {operation}: updated User {result.UserId}.");
            return 0;
        }

        string message = result.Status switch
        {
            PasswordCredentialMutationStatus.UserNotFound => "the User does not exist.",
            PasswordCredentialMutationStatus.CredentialNotFound => "the User has no Password credential.",
            PasswordCredentialMutationStatus.EmailRequired =>
                "--email is required when attaching the User's first Password credential.",
            PasswordCredentialMutationStatus.EmailNotAllowed =>
                "--email is only valid when attaching the User's first Password credential; use change-email separately.",
            PasswordCredentialMutationStatus.EmailConflict =>
                "another Password credential already uses that sign-in email.",
            PasswordCredentialMutationStatus.ConcurrentConflict =>
                "the Password credential changed concurrently; retry the command.",
            _ => "the operation failed.",
        };
        Console.Error.WriteLine($"operator-user {operation}: {message}");
        return 1;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  operator-user list");
        Console.Error.WriteLine("  operator-user create --display-name <name> --email <email>");
        Console.Error.WriteLine("  operator-user set-password --user-id <id> [--email <email>]");
        Console.Error.WriteLine("  operator-user change-email --user-id <id> --email <email>");
        Console.Error.WriteLine("  operator-user disable-password --user-id <id>");
        return 2;
    }

    private sealed class OperatorUserConsole : IOperatorUserConsole
    {
        public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
        public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);
        public void Write(string value) => Console.Write(value);
        public void WriteLine(string value = "") => Console.WriteLine(value);
        public void WriteError(string value) => Console.Error.WriteLine(value);
    }
}
