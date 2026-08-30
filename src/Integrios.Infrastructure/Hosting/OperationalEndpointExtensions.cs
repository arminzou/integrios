using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Integrios.Infrastructure.Hosting;

public static class OperationalEndpointExtensions
{
    public const int DefaultPort = 5299;

    public static int AddOperationalEndpoints(
        this WebApplicationBuilder builder,
        string portConfigurationKey,
        bool operationalOnly = false)
    {
        int port = builder.Configuration.GetValue(portConfigurationKey, DefaultPort);
        if (port is < 1 or > 65535)
            throw new InvalidOperationException($"{portConfigurationKey} must be a valid TCP port.");

        if (operationalOnly)
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        else
            builder.WebHost.UseUrls([.. ConfiguredProductUrls(builder), $"http://0.0.0.0:{port}"]);

        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseReadinessHealthCheck>(
                "database",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: TimeSpan.FromSeconds(5));

        return port;
    }

    public static WebApplication UseOperationalEndpointIsolation(this WebApplication app, int operationalPort)
    {
        app.Use(async (context, next) =>
        {
            // TestServer has no TCP listener. Packaged-host tests prove real listener isolation.
            if (context.Connection.LocalPort != 0)
            {
                bool isOperationalListener = context.Connection.LocalPort == operationalPort;
                bool isOperationalEndpoint = context.GetEndpoint()?.Metadata
                    .GetMetadata<OperationalEndpointMetadata>() is not null;

                if (isOperationalListener != isOperationalEndpoint)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            await next(context);
        });

        return app;
    }

    public static WebApplication MapOperationalEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
            {
                Predicate = _ => false
            })
            .WithMetadata(OperationalEndpointMetadata.Instance);

        app.MapHealthChecks("/ready", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("ready")
            })
            .WithMetadata(OperationalEndpointMetadata.Instance);

        app.MapPrometheusScrapingEndpoint()
            .WithMetadata(OperationalEndpointMetadata.Instance);

        return app;
    }

    private static IEnumerable<string> ConfiguredProductUrls(WebApplicationBuilder builder)
    {
        string? urls = builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey);
        if (!string.IsNullOrWhiteSpace(urls))
            return urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var configured = new List<string>();
        AddPorts(configured, builder.WebHost.GetSetting(WebHostDefaults.HttpPortsKey), "http");
        AddPorts(configured, builder.WebHost.GetSetting(WebHostDefaults.HttpsPortsKey), "https");
        return configured.Count == 0 ? ["http://localhost:5000"] : configured;
    }

    private static void AddPorts(List<string> urls, string? ports, string scheme)
    {
        if (string.IsNullOrWhiteSpace(ports))
            return;

        urls.AddRange(ports
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(port => $"{scheme}://0.0.0.0:{port}"));
    }

    private sealed class OperationalEndpointMetadata
    {
        public static readonly OperationalEndpointMetadata Instance = new();
    }
}

internal sealed class DatabaseReadinessHealthCheck(IDbConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Database connection failed.", exception);
        }
    }
}
