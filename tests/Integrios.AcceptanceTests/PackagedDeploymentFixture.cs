using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Integrios.Tests.Shared;
using Npgsql;

namespace Integrios.AcceptanceTests;

public sealed class PackagedDeploymentFixture : IAsyncLifetime
{
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    private readonly string repoRoot = ResolveRepoRoot();
    private readonly string projectName = $"integrios-q-{Guid.NewGuid():N}"[..25];
    private readonly string otelArtifactsDirectory = Path.Combine(Path.GetTempPath(), $"integrios-otel-{Guid.NewGuid():N}");
    private readonly string secretsDirectory = Path.Combine(Path.GetTempPath(), $"integrios-secrets-{Guid.NewGuid():N}");
    private readonly string sourceVerificationSecretsDirectory = Path.Combine(
        Path.GetTempPath(),
        $"integrios-source-secrets-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> environment;
    private IReadOnlyList<string> composeFiles;

    private readonly int postgresPort = GetAvailablePort();
    private readonly int ingestionPort = GetAvailablePort();
    private readonly int ingestionOperationalPort = GetAvailablePort();
    private readonly int adminPort = GetAvailablePort();
    private readonly int adminOperationalPort = GetAvailablePort();
    private readonly int mockSinkPort = GetAvailablePort();
    private readonly int workerMetricsPort = GetAvailablePort();

    public PackagedDeploymentFixture()
    {
        Directory.CreateDirectory(otelArtifactsDirectory);
        Directory.CreateDirectory(secretsDirectory);
        Directory.CreateDirectory(sourceVerificationSecretsDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                otelArtifactsDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
        composeFiles = [Path.Combine(repoRoot, "compose.yml")];
        environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["INTEGRIOS_POSTGRES_PORT"] = postgresPort.ToString(),
            ["INTEGRIOS_INGESTION_PORT"] = ingestionPort.ToString(),
            ["INTEGRIOS_INGESTION_OPERATIONAL_PORT"] = ingestionOperationalPort.ToString(),
            ["INTEGRIOS_ADMIN_PORT"] = adminPort.ToString(),
            ["INTEGRIOS_ADMIN_OPERATIONAL_PORT"] = adminOperationalPort.ToString(),
            ["INTEGRIOS_MOCKSINK_PORT"] = mockSinkPort.ToString(),
            ["INTEGRIOS_WIREMOCK_MAPPINGS_DIR"] = Path.Combine(repoRoot, "tests", "Integrios.AcceptanceTests", "wiremock", "mappings"),
            ["INTEGRIOS_WORKER_METRICS_PORT"] = workerMetricsPort.ToString(),
            ["INTEGRIOS_OTEL_CONFIG"] = Path.Combine(repoRoot, "tests", "Integrios.AcceptanceTests", "otel-collector.acceptance.yaml"),
            ["INTEGRIOS_OTEL_ARTIFACTS_DIR"] = otelArtifactsDirectory,
            ["INTEGRIOS_DESTINATION_SECRETS_DIR"] = secretsDirectory,
            ["INTEGRIOS_SOURCE_SECRETS_DIR"] = sourceVerificationSecretsDirectory,
            ["POSTGRES_USER"] = "integrios",
            ["POSTGRES_PASSWORD"] = "acceptance_postgres",
            ["INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET"] = "acceptance-admin-secret",
            ["INTEGRIOS_PUBLIC_INGESTION_BASE_URI"] = "https://acceptance.example.test",
            // Admin maps the dashboard only when an identity provider is configured, so the
            // packaged run configures one to prove the browser surface is actually served. No
            // sign-in happens here and this authority is never reached: the real provider round
            // trip is a Functional gate against a containerized provider.
            ["INTEGRIOS_ADMIN_OIDC_AUTHORITY"] = "https://oidc.invalid/",
            ["INTEGRIOS_ADMIN_OIDC_CLIENT_ID"] = "integrios-acceptance",
            ["INTEGRIOS_BOOTSTRAP_IMAGE"] = $"{projectName}-bootstrap",
            ["INTEGRIOS_ADMIN_IMAGE"] = $"{projectName}-admin",
            ["INTEGRIOS_INGESTION_IMAGE"] = $"{projectName}-ingestion",
            ["INTEGRIOS_WORKER_IMAGE"] = $"{projectName}-worker",
        };
    }

    /// The images this deployment built, so a test can assert what an artifact carries rather than
    /// only what a running deployment happens to serve.
    public string AdminImage => environment["INTEGRIOS_ADMIN_IMAGE"];
    public string BootstrapImage => environment["INTEGRIOS_BOOTSTRAP_IMAGE"];
    public string IngestionImage => environment["INTEGRIOS_INGESTION_IMAGE"];
    public string WorkerImage => environment["INTEGRIOS_WORKER_IMAGE"];

    public HttpClient AdminClient { get; private set; } = null!;
    public HttpClient AdminOperationalClient { get; private set; } = null!;
    public HttpClient IngestionClient { get; private set; } = null!;
    public HttpClient IngestionOperationalClient { get; private set; } = null!;
    private HttpClient wireMockClient = null!;
    public WireMockSink WireMockSink { get; private set; } = null!;
    public HttpClient WorkerOperationalClient { get; private set; } = null!;
    public string AdminAuthorization { get; private set; } = "OperatorKey global_operator_key:acceptance-admin-secret";
    public Guid HttpConnectorId { get; private set; }
    public Guid ApiKeyConnectorId { get; private set; }
    public Guid BearerConnectorId { get; private set; }
    public Guid SourceOnlyConnectorId { get; private set; }

    public string ConnectionString => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = postgresPort,
        Database = "integrios",
        Username = "integrios",
        Password = "acceptance_postgres",
        Timeout = 5,
        Pooling = false,
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        try
        {
            await StartDeploymentAsync(buildImages: true);
            await AssertServerEnvironmentsAsync("Development");
            await AssertBootstrapStateAsync();
            DisposeClients();
            await StopDeploymentAsync();

            composeFiles =
            [
                Path.Combine(repoRoot, "deploy", "compose.yml"),
                Path.Combine(repoRoot, "tests", "Integrios.AcceptanceTests", "compose.acceptance.yml"),
            ];
            await StartDeploymentAsync(buildImages: false);
            await AssertServerEnvironmentsAsync("Production");
            await AssertBootstrapStateAsync();
            HttpConnectorId = await ApplyExampleManifestAsync("http");
            ApiKeyConnectorId = await ApplyConnectorManifestAsync(
                "acceptance_api_key",
                TestConnectorManifest.Create("acceptance_api_key", "Acceptance API key", "destination", ["api_key_header"]));
            BearerConnectorId = await ApplyConnectorManifestAsync(
                "acceptance_bearer",
                TestConnectorManifest.Create("acceptance_bearer", "Acceptance bearer", "destination", ["bearer_token"]));
            SourceOnlyConnectorId = await ApplyConnectorManifestAsync(
                "acceptance_source",
                TestConnectorManifest.Create("acceptance_source", "Acceptance source", "source"));
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
            Directory.Delete(otelArtifactsDirectory, recursive: true);
            Directory.Delete(secretsDirectory, recursive: true);
            Directory.Delete(sourceVerificationSecretsDirectory, recursive: true);
        }
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        if (value is null)
            throw new InvalidOperationException("Query returned null.");
        return value is T typed
            ? typed
            : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public async Task<Guid> ApplyExampleManifestAsync(string key)
    {
        string path = Path.Combine(repoRoot, "examples", "connectors", $"{key}.json");
        return await ApplyConnectorManifestAsync(key, await File.ReadAllTextAsync(path));
    }

    private async Task<Guid> ApplyConnectorManifestAsync(string key, string manifest)
    {
        using var content = new StringContent(manifest, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/admin/connectors/{key}/versions/1")
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("Authorization", AdminAuthorization);
        using HttpResponseMessage response = await AdminClient.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        if (response.StatusCode is not HttpStatusCode.Created and not HttpStatusCode.OK)
            throw new InvalidOperationException($"Applying {key}.json failed with {(int)response.StatusCode}: {responseBody}");

        using JsonDocument document = JsonDocument.Parse(responseBody);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task WriteSecretAsync(string tenantSlug, string reference, string value)
    {
        string tenantDirectory = Path.Combine(secretsDirectory, tenantSlug);
        Directory.CreateDirectory(tenantDirectory);
        await File.WriteAllTextAsync(Path.Combine(tenantDirectory, reference), value);
    }

    public async Task WriteSourceSecretAsync(string tenantSlug, string reference, string value)
    {
        string tenantDirectory = Path.Combine(sourceVerificationSecretsDirectory, tenantSlug);
        Directory.CreateDirectory(tenantDirectory);
        await File.WriteAllTextAsync(Path.Combine(tenantDirectory, reference), value);
    }

    public async Task CreateBlockingSecretPipeAsync(string tenantSlug, string reference)
    {
        string tenantDirectory = Path.Combine(secretsDirectory, tenantSlug);
        Directory.CreateDirectory(tenantDirectory);
        string path = Path.Combine(tenantDirectory, reference);
        File.Delete(path);

        string containerPath = $"/var/lib/integrios-acceptance/secrets/{tenantSlug}/{reference}";
        ComposeResult result = await RunComposeAsync(
            TimeSpan.FromSeconds(30),
            "exec",
            "--no-TTY",
            "postgres",
            "mkfifo",
            containerPath);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not create blocking secret pipe: {result.Output}");
    }

    public async Task ReplaceSecretWithFileAsync(string tenantSlug, string reference, string value)
    {
        string path = Path.Combine(secretsDirectory, tenantSlug, reference);
        File.Delete(path);
        await File.WriteAllTextAsync(path, value);
    }

    // Rotation must be atomic: staging the new link and renaming it over the reference leaves no
    // window in which the reference is absent. A delete-then-create would let a Worker poll land in
    // the gap and record a spurious secret_resolution failure.
    public void RotateSecretSymlink(string tenantSlug, string reference, string targetName, string value)
    {
        string tenantDirectory = Path.Combine(secretsDirectory, tenantSlug);
        Directory.CreateDirectory(tenantDirectory);
        File.WriteAllText(Path.Combine(tenantDirectory, targetName), value);

        string staging = Path.Combine(tenantDirectory, $".{reference}.staging");
        if (File.Exists(staging))
            File.Delete(staging);
        File.CreateSymbolicLink(staging, targetName);
        File.Move(staging, Path.Combine(tenantDirectory, reference), overwrite: true);
    }

    // Deployment-wide and irreversible for the rest of the fixture lifetime: the Worker keeps the
    // new provider for every later test in this collection, and its metrics counters restart from
    // zero. Test classes sharing this fixture must not assume a pre-recreation Worker.
    public async Task RecreateWorkerAsync(
        string provider,
        string? configurationSharedSecret = null,
        string? configurationOnlySecret = null)
    {
        environment["INTEGRIOS_DESTINATION_SECRETS_PROVIDER"] = provider;
        environment["INTEGRIOS_ACCEPTANCE_CONFIG_SHARED_SECRET"] = configurationSharedSecret ?? string.Empty;
        environment["INTEGRIOS_ACCEPTANCE_CONFIG_ONLY_SECRET"] = configurationOnlySecret ?? string.Empty;

        ComposeResult result = await RunComposeAsync(
            TimeSpan.FromMinutes(2),
            "up",
            "--detach",
            "--force-recreate",
            "worker");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not recreate Worker: {result.Output}");

        await WaitForServiceAsync("worker");
    }

    public async Task<ComposeResult> RunWorkerCommandAsync(
        IReadOnlyDictionary<string, string?> commandEnvironment,
        params string[] arguments)
    {
        var composeArguments = new List<string> { "run", "--rm", "--no-deps" };
        foreach ((string key, string? value) in commandEnvironment)
        {
            composeArguments.Add("-e");
            composeArguments.Add($"{key}={value ?? string.Empty}");
        }
        composeArguments.Add("worker");
        composeArguments.AddRange(arguments);
        return await RunComposeAsync(TimeSpan.FromMinutes(2), composeArguments.ToArray());
    }

    // Revokes the bootstrap OperatorKey deployment-wide. Every later control-plane call in this
    // collection must authenticate through AdminAuthorization rather than a captured header value.
    public async Task<string> RotateOperatorKeyAsync(string replacementSecret)
    {
        ComposeResult result = await RunComposeAsync(
            TimeSpan.FromMinutes(2),
            "run",
            "--rm",
            "--no-deps",
            "-e",
            $"INTEGRIOS_OPERATOR_KEY_ROTATION_SECRET={replacementSecret}",
            "admin",
            "operator-key",
            "rotate");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"OperatorKey rotation failed: {result.Output}");
        if (result.Output.Contains(replacementSecret, StringComparison.Ordinal))
            throw new InvalidOperationException("OperatorKey rotation disclosed the replacement secret.");

        Match match = Regex.Match(result.StandardOutput, @"Public key:\s*(?<key>[a-zA-Z0-9_]+)");
        if (!match.Success)
            throw new InvalidOperationException($"OperatorKey rotation did not print a public identifier: {result.Output}");

        string publicKey = match.Groups["key"].Value;
        AdminAuthorization = $"OperatorKey {publicKey}:{replacementSecret}";
        return publicKey;
    }

    public async Task RunBootstrapAgainAsync()
    {
        ComposeResult result = await RunComposeAsync(TimeSpan.FromMinutes(2), "run", "--rm", "bootstrap");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Bootstrap rerun failed: {result.Output}");
    }

    public async Task RestartProductServicesAsync()
    {
        ComposeResult result = await RunComposeAsync(
            TimeSpan.FromMinutes(2),
            "restart",
            "admin",
            "ingestion",
            "worker");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Service restart failed: {result.Output}");
        await WaitUntilReadyAsync(expectCollector: true);
    }

    public async Task KillWorkerAsync()
    {
        ComposeResult result = await RunComposeAsync(TimeSpan.FromSeconds(30), "kill", "--signal", "SIGKILL", "worker");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not kill Worker: {result.Output}");
    }

    public async Task StartWorkerAsync()
    {
        ComposeResult result = await RunComposeAsync(TimeSpan.FromMinutes(2), "start", "worker");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not start Worker: {result.Output}");
        await WaitForServiceAsync("worker");
    }

    public async Task<string> StartAdditionalWorkerAsync()
    {
        string containerName = $"{projectName}-worker-extra-{Guid.NewGuid():N}"[..50];
        ComposeResult result = await RunComposeAsync(
            TimeSpan.FromMinutes(2),
            "run",
            "--detach",
            "--no-deps",
            "--name",
            containerName,
            "worker");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not start additional Worker: {result.Output}");
        return containerName;
    }

    public async Task RemoveContainerAsync(string containerName)
    {
        ComposeResult result = await RunDockerAsync(
            TimeSpan.FromSeconds(30),
            ["rm", "--force", containerName]);
        if (result.ExitCode != 0 && !result.Output.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not remove container '{containerName}': {result.Output}");
    }

    public async Task<string> GetContainerLogsAsync(string containerName)
    {
        ComposeResult result = await RunDockerAsync(TimeSpan.FromSeconds(30), ["logs", containerName]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not read container '{containerName}' logs: {result.Output}");
        return result.Output;
    }

    // The packaged observability fact removes the collector to prove Delivery is indifferent to it.
    public async Task StopCollectorAsync()
    {
        ComposeResult result = await RunComposeAsync(TimeSpan.FromSeconds(60), "stop", "otel-collector");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not stop the OTLP collector: {result.Output}");
    }

    public async Task StartCollectorAsync()
    {
        ComposeResult result = await RunComposeAsync(TimeSpan.FromMinutes(2), "start", "otel-collector");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not start the OTLP collector: {result.Output}");
        await WaitForServiceAsync("otel-collector");
    }

    public async Task RestartPostgresAsync()
    {
        ComposeResult result = await RunComposeAsync(TimeSpan.FromMinutes(2), "restart", "postgres");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not restart Postgres: {result.Output}");

        await WaitForPostgresAsync();
    }

    public async Task StopPostgresAsync()
    {
        ComposeResult result = await RunComposeAsync(TimeSpan.FromMinutes(2), "stop", "postgres");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not stop Postgres: {result.Output}");
    }

    public async Task StartPostgresAsync()
    {
        ComposeResult result = await RunComposeAsync(TimeSpan.FromMinutes(2), "start", "postgres");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not start Postgres: {result.Output}");

        await WaitForPostgresAsync();
        await WaitUntilReadyAsync(expectCollector: true);
    }

    private async Task WaitForPostgresAsync()
    {
        var deadline = Stopwatch.StartNew();
        Exception? lastException = null;
        while (deadline.Elapsed < ReadinessTimeout)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        throw new TimeoutException($"Postgres did not become available: {lastException?.Message}");
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
            "ingestion",
            "admin",
            "worker",
            "mocksink",
        ]);
        if (!buildImages)
            arguments.Add("otel-collector");

        ComposeResult startup = await RunComposeAsync(ComposeTimeout, arguments.ToArray());
        if (startup.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker Compose startup failed with exit code {startup.ExitCode}."
                + Environment.NewLine
                + startup.Output);
        }

        AdminClient = CreateClient(adminPort);
        AdminOperationalClient = CreateClient(adminOperationalPort);
        IngestionClient = CreateClient(ingestionPort);
        IngestionOperationalClient = CreateClient(ingestionOperationalPort);
        wireMockClient = CreateClient(mockSinkPort);
        WireMockSink = new WireMockSink(wireMockClient);
        WorkerOperationalClient = CreateClient(workerMetricsPort);
        await WaitUntilReadyAsync(expectCollector: !buildImages);
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

    private async Task WaitForServiceAsync(string serviceName)
    {
        var deadline = Stopwatch.StartNew();
        string lastObservation = "No service status available.";
        while (deadline.Elapsed < ReadinessTimeout)
        {
            ComposeResult status = await RunComposeAsync(
                TimeSpan.FromSeconds(15),
                "ps",
                "--status",
                "running",
                "--services",
                serviceName);
            lastObservation = status.Output;
            if (status.ExitCode == 0
                && status.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Contains(serviceName, StringComparer.Ordinal))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException($"{serviceName} did not become ready. Last observation: {lastObservation}");
    }

    private async Task AssertBootstrapStateAsync()
    {
        long connectors = await ScalarAsync<long>("SELECT COUNT(*) FROM connectors");
        long liveOperatorKeys = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM operator_keys WHERE revoked_at IS NULL");
        if (connectors != 0 || liveOperatorKeys != 1)
        {
            throw new InvalidOperationException(
                $"Packaged Bootstrap state was unexpected: Connectors={connectors}, live OperatorKeys={liveOperatorKeys}.");
        }
    }

    private async Task AssertServerEnvironmentsAsync(string expected)
    {
        foreach (string serviceName in (string[])["admin", "ingestion", "worker"])
        {
            ComposeResult result = await RunComposeAsync(
                TimeSpan.FromSeconds(30),
                "exec",
                "--no-TTY",
                serviceName,
                "printenv",
                "DOTNET_ENVIRONMENT");
            if (result.ExitCode != 0 || result.StandardOutput.Trim() != expected)
            {
                throw new InvalidOperationException(
                    $"{serviceName} did not declare DOTNET_ENVIRONMENT={expected}: {result.Output}");
            }
        }
    }

    private async Task WaitUntilReadyAsync(bool expectCollector)
    {
        var deadline = Stopwatch.StartNew();
        string lastObservation = "No readiness check completed.";

        while (deadline.Elapsed < ReadinessTimeout)
        {
            try
            {
                await AssertHealthyAsync(AdminOperationalClient, "Admin");
                await AssertHealthyAsync(AdminOperationalClient, "Admin", "/ready");
                await AssertHealthyAsync(IngestionOperationalClient, "Ingestion");
                await AssertHealthyAsync(IngestionOperationalClient, "Ingestion", "/ready");
                await AssertHealthyAsync(WorkerOperationalClient, "Worker");
                await AssertHealthyAsync(WorkerOperationalClient, "Worker", "/ready");
                await AssertHealthyAsync(wireMockClient, "WireMock", "/__admin/health");

                // The OTLP receiver must accept spans before a test emits any, or early spans are
                // dropped and the trace-continuity assertions fail with no usable diagnostic.
                if (expectCollector)
                    await AssertCollectorReceivingAsync();

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
                    && services.Contains("ingestion", StringComparer.Ordinal)
                    && services.Contains("worker", StringComparer.Ordinal)
                    && services.Contains("mocksink", StringComparer.Ordinal)
                    && (!expectCollector || services.Contains("otel-collector", StringComparer.Ordinal)))
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

    // Readiness comes from the pinned collector's own startup line rather than a published health
    // port, so the harness does not draw another host port it would have to win a race for.
    private async Task AssertCollectorReceivingAsync()
    {
        string logs = await GetServiceLogsAsync("otel-collector");
        if (!logs.Contains("Everything is ready", StringComparison.Ordinal))
            throw new InvalidOperationException("OTLP collector has not reported readiness yet.");
    }

    private static async Task AssertHealthyAsync(HttpClient client, string serviceName, string path = "/health")
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{serviceName} {path} returned {(int)response.StatusCode}.");
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
            environment["INTEGRIOS_INGESTION_IMAGE"],
            environment["INTEGRIOS_WORKER_IMAGE"],
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

    /// Names of the entries an image carries directly under `path`, empty when the directory is
    /// absent or empty. Runs the image with a shell rather than its own entrypoint, so nothing about
    /// the service starts.
    public async Task<IReadOnlyList<string>> ListImageEntriesAsync(string image, string path)
    {
        ComposeResult result = await RunDockerAsync(
            TimeSpan.FromMinutes(1),
            ["run", "--rm", "--entrypoint", "sh", image, "-c", $"ls -A '{path}' 2>/dev/null || true"]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not list '{path}' in {image}: {result.Output}");

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// True when the image can run the named executable at all. Used to prove a host carries no
    /// build toolchain rather than merely not invoking one.
    public async Task<bool> ImageHasExecutableAsync(string image, string executable)
    {
        ComposeResult result = await RunDockerAsync(
            TimeSpan.FromMinutes(1),
            ["run", "--rm", "--entrypoint", "sh", image, "-c", $"command -v '{executable}' >/dev/null && echo present || echo absent"]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not probe {executable} in {image}: {result.Output}");

        return result.StandardOutput.Contains("present", StringComparison.Ordinal);
    }

    private void DisposeClients()
    {
        AdminClient?.Dispose();
        AdminOperationalClient?.Dispose();
        IngestionClient?.Dispose();
        IngestionOperationalClient?.Dispose();
        wireMockClient?.Dispose();
        WireMockSink = null!;
        WorkerOperationalClient?.Dispose();
    }

    public async Task<string> ReadTraceArtifactsAsync()
    {
        ComposeResult result = await RunComposeAsync(
            TimeSpan.FromSeconds(30),
            "exec",
            "--no-TTY",
            "postgres",
            "cat",
            "/var/lib/integrios-acceptance/otel/traces.jsonl");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not read trace artifacts: {result.Output}");
        return result.StandardOutput;
    }

    public async Task<string> GetServiceLogsAsync(string serviceName)
    {
        ComposeResult logs = await RunComposeAsync(
            TimeSpan.FromSeconds(30),
            "logs",
            "--no-color",
            serviceName);
        if (logs.ExitCode != 0)
            throw new InvalidOperationException($"Could not read {serviceName} logs: {logs.Output}");
        return logs.Output;
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
