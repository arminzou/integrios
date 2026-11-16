using System.Diagnostics;

namespace Integrios.UnitTests;

public sealed class SecretValidationProcessTests
{
    [Theory]
    [InlineData("ingestion")]
    [InlineData("worker")]
    public async Task UnreachableKeyVault_FailsStartupWithUsageExitCodeWithoutStackTrace(string role)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(typeof(HostSelector).Assembly.Location);
        startInfo.ArgumentList.Add(role);
        startInfo.ArgumentList.Add("secrets");
        startInfo.ArgumentList.Add("validate");
        startInfo.ArgumentList.Add("--all");
        startInfo.Environment["ConnectionStrings__Postgres"] = "Host=localhost;Database=integrios;Username=test;Password=test";
        startInfo.Environment["Integrios__KeyVault__Uri"] = "https://integrios-unreachable.invalid/";

        using Process process = Process.Start(startInfo)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // The vault loads before the host is built, so its failure precedes any database work;
        // output from that work would mean the vault source was never added.
        process.ExitCode.ShouldBe(2);
        (await standardOutput).ShouldBeEmpty();
        (await standardError).Trim().ShouldBe("Secret validation could not start with the current configuration.");
    }
}
