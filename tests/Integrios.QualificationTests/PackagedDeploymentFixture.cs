using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Integrios.QualificationTests;

public sealed class PackagedDeploymentFixture : IAsyncLifetime
{
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    private readonly string repoRoot = ResolveRepoRoot();
    private readonly string projectName = $"integrios-q-{Guid.NewGuid():N}"[..25];
    private readonly Dictionary<string, string> environment;
    private IReadOnlyList<string> composeFiles;

    private readonly int postgresPort = GetAvailablePort();
    private readonly int ingressPort = GetAvailablePort();
    private readonly int adminPort = GetAvailablePort();
    private readonly int mockSinkPort = GetAvailablePort();

    public PackagedDeploymentFixture()
    {
        composeFiles = [Path.Combine(repoRoot, "compose.yml")];
        environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["INTEGRIOS_POSTGRES_PORT"] = postgresPort.ToString(),
            ["INTEGRIOS_INGRESS_PORT"] = ingressPort.ToString(),
            ["INTEGRIOS_ADMIN_PORT"] = adminPort.ToString(),
            ["INTEGRIOS_MOCKSINK_PORT"] = mockSinkPort.ToString(),
            ["POSTGRES_USER"] = "integrios",
            ["POSTGRES_PASSWORD"] = "qualification_postgres",
            ["INTEGRIOS_BOOTSTRAP_ADMIN_SECRET"] = "qualification-admin-secret",
            ["INTEGRIOS_BOOTSTRAP_IMAGE"] = $"{projectName}-bootstrap",
            ["INTEGRIOS_ADMIN_IMAGE"] = $"{projectName}-admin",
            ["INTEGRIOS_INGRESS_IMAGE"] = $"{projectName}-ingress",
            ["INTEGRIOS_WORKER_IMAGE"] = $"{projectName}-worker",
            ["INTEGRIOS_MOCKSINK_IMAGE"] = $"{projectName}-mocksink",
        };
    }

    public HttpClient AdminClient { get; private set; } = null!;
    public HttpClient IngressClient { get; private set; } = null!;
    public HttpClient MockSinkClient { get; private set; } = null!;

    public string ConnectionString => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = postgresPort,
        Database = "integrios",
        Username = "integrios",
        Password = "qualification_postgres",
        Timeout = 5,
        Pooling = false,
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        try
        {
            await StartDeploymentAsync(buildImages: true);
            await AssertBootstrapStateAsync();
            DisposeClients();
            await StopDeploymentAsync();

            composeFiles =
            [
                Path.Combine(repoRoot, "deploy", "compose.yml"),
                Path.Combine(repoRoot, "tests", "Integrios.QualificationTests", "compose.deploy.qualification.yml"),
            ];
            await StartDeploymentAsync(buildImages: false);
        }
        catch (Exception exception)
        {
            string diagnostics = await CaptureDiagnosticsAsync();
            await CleanupBestEffortAsync();
            throw new InvalidOperationException(
                $"Packaged deployment '{projectName}' did not become ready.{Environment.NewLine}"
                + $"Cause: {exception.Message}{Environment.NewLine}{diagnostics}",
                exception);
        }
    }

    public async Task DisposeAsync()
    {
        DisposeClients();
        try
        {
            await StopDeploymentAsync();
        }
        finally
        {
            await RemoveImagesBestEffortAsync();
        }
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return (T)(value ?? throw new InvalidOperationException("Query returned null."));
    }

    private async Task StartDeploymentAsync(bool buildImages)
    {
        var arguments = new List<string> { "up" };
        if (buildImages)
            arguments.Add("--build");
        arguments.AddRange(
        [
            "--detach",
            "--remove-orphans",
            "postgres",
            "migrate",
            "bootstrap",
            "ingress",
            "admin",
            "worker",
            "mocksink",
        ]);

        ComposeResult startup = await RunComposeAsync(ComposeTimeout, arguments.ToArray());
        if (startup.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker Compose startup failed with exit code {startup.ExitCode}."
                + Environment.NewLine
                + startup.Output);
        }

        AdminClient = CreateClient(adminPort);
        IngressClient = CreateClient(ingressPort);
        MockSinkClient = CreateClient(mockSinkPort);
        await WaitUntilReadyAsync();
    }

    private async Task StopDeploymentAsync()
    {
        ComposeResult cleanup = await RunComposeAsync(
            TimeSpan.FromMinutes(2),
            "down",
            "--volumes",
            "--remove-orphans",
            "--timeout",
            "10");

        if (cleanup.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker Compose cleanup failed for '{projectName}' with exit code {cleanup.ExitCode}."
                + Environment.NewLine
                + cleanup.Output);
        }
    }

    private async Task AssertBootstrapStateAsync()
    {
        long builtins = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM integrations WHERE key = 'webhook' AND status = 'active'");
        long liveAdminKeys = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM admin_keys WHERE revoked_at IS NULL");
        if (builtins != 1 || liveAdminKeys != 1)
        {
            throw new InvalidOperationException(
                $"Packaged Bootstrap state was unexpected: built-ins={builtins}, live AdminKeys={liveAdminKeys}.");
        }
    }

    private async Task WaitUntilReadyAsync()
    {
        var deadline = Stopwatch.StartNew();
        string lastObservation = "No readiness check completed.";

        while (deadline.Elapsed < ReadinessTimeout)
        {
            try
            {
                await AssertHealthyAsync(AdminClient, "Admin");
                await AssertHealthyAsync(IngressClient, "Ingress");
                await AssertHealthyAsync(MockSinkClient, "MockSink");

                await using (var connection = new NpgsqlConnection(ConnectionString))
                    await connection.OpenAsync();

                ComposeResult runningServices = await RunComposeAsync(
                    TimeSpan.FromSeconds(15),
                    "ps",
                    "--status",
                    "running",
                    "--services");
                string[] services = runningServices.StandardOutput.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (runningServices.ExitCode == 0
                    && services.Contains("admin", StringComparer.Ordinal)
                    && services.Contains("ingress", StringComparer.Ordinal)
                    && services.Contains("worker", StringComparer.Ordinal)
                    && services.Contains("mocksink", StringComparer.Ordinal))
                {
                    return;
                }

                lastObservation = $"Running services: {string.Join(", ", services)}";
            }
            catch (Exception exception)
            {
                lastObservation = exception.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException(
            $"Services were not ready within {ReadinessTimeout}. Last observation: {lastObservation}");
    }

    private static async Task AssertHealthyAsync(HttpClient client, string serviceName)
    {
        using HttpResponseMessage response = await client.GetAsync("/health");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{serviceName} /health returned {(int)response.StatusCode}.");
    }

    private async Task<string> CaptureDiagnosticsAsync()
    {
        try
        {
            ComposeResult status = await RunComposeAsync(
                TimeSpan.FromSeconds(30),
                "ps",
                "--all");
            ComposeResult logs = await RunComposeAsync(
                TimeSpan.FromSeconds(30),
                "logs",
                "--no-color",
                "--tail",
                "200");

            return $"Compose status:{Environment.NewLine}{status.Output}{Environment.NewLine}"
                + $"Compose logs (last 200 lines per service):{Environment.NewLine}{logs.Output}";
        }
        catch (Exception exception)
        {
            return $"Diagnostics collection also failed: {exception.Message}";
        }
    }

    private async Task CleanupBestEffortAsync()
    {
        try
        {
            await RunComposeAsync(
                TimeSpan.FromMinutes(2),
                "down",
                "--volumes",
                "--remove-orphans",
                "--timeout",
                "10");
        }
        catch
        {
            // Preserve the startup failure and its diagnostics.
        }

        await RemoveImagesBestEffortAsync();
    }

    private async Task RemoveImagesBestEffortAsync()
    {
        string[] imageNames =
        [
            environment["INTEGRIOS_BOOTSTRAP_IMAGE"],
            environment["INTEGRIOS_ADMIN_IMAGE"],
            environment["INTEGRIOS_INGRESS_IMAGE"],
            environment["INTEGRIOS_WORKER_IMAGE"],
            environment["INTEGRIOS_MOCKSINK_IMAGE"],
        ];

        try
        {
            await RunDockerAsync(
                TimeSpan.FromMinutes(2),
                ["image", "rm", .. imageNames]);
        }
        catch
        {
            // Image cleanup is best effort so it cannot hide the primary test failure.
        }
    }

    private void DisposeClients()
    {
        AdminClient?.Dispose();
        IngressClient?.Dispose();
        MockSinkClient?.Dispose();
    }

    private async Task<ComposeResult> RunComposeAsync(TimeSpan timeout, params string[] arguments)
    {
        var dockerArguments = new List<string>
        {
            "compose",
            "--project-name",
            projectName,
        };
        foreach (string composeFile in composeFiles)
        {
            dockerArguments.Add("--file");
            dockerArguments.Add(composeFile);
        }
        foreach (string argument in arguments)
            dockerArguments.Add(argument);

        return await RunDockerAsync(timeout, dockerArguments);
    }

    private async Task<ComposeResult> RunDockerAsync(
        TimeSpan timeout,
        IReadOnlyCollection<string> arguments)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach ((string key, string value) in environment)
            startInfo.Environment[key] = value;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Docker Compose.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Docker Compose did not exit within {timeout}.");
        }

        return new ComposeResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static HttpClient CreateClient(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Integrios repository root.");
    }
}

public sealed record ComposeResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
