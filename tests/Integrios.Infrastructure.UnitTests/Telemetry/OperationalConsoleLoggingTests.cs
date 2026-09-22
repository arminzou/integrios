using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Integrios.Infrastructure.UnitTests;

public sealed class OperationalConsoleLoggingTests
{
    [Theory]
    [InlineData(false, "json")]
    [InlineData(true, "simple")]
    public void Registration_SelectsTheEnvironmentFormatter(bool isDevelopment, string formatterName)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOperationalConsoleLogging(isDevelopment));

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName
            .ShouldBe(formatterName);

        provider.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value.ActivityTrackingOptions
            .ShouldBe(ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        if (isDevelopment)
        {
            provider.GetRequiredService<IOptions<SimpleConsoleFormatterOptions>>().Value.IncludeScopes
                .ShouldBeTrue();
        }
        else
        {
            JsonConsoleFormatterOptions json =
                provider.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>().Value;
            json.IncludeScopes.ShouldBeTrue();
            json.UseUtcTimestamp.ShouldBeTrue();
            json.TimestampFormat.ShouldNotBeNullOrWhiteSpace();
            DateTimeOffset.UtcNow.ToString(json.TimestampFormat).ShouldEndWith("Z");
        }
    }

    [Theory]
    [InlineData("/health", false)]
    [InlineData("/ready", false)]
    [InlineData("/metrics", false)]
    [InlineData("/favicon.ico", false)]
    [InlineData("/api/events", true)]
    public void TelemetryRegistration_ExcludesOperationalEndpointsFromExportedTraces(string path, bool exported)
    {
        var services = new ServiceCollection();
        services.AddTelemetryServices(new ConfigurationBuilder().Build(), "integrios-admin");

        using ServiceProvider provider = services.BuildServiceProvider();
        // Resolving TracerProvider applies the registered instrumentation options.
        provider.GetRequiredService<TracerProvider>();

        Func<HttpContext, bool>? filter = provider
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName)
            .Filter;

        filter.ShouldNotBeNull();
        filter(new DefaultHttpContext { Request = { Path = path } }).ShouldBe(exported);
    }

    [Fact]
    public void TelemetryRegistration_RemovesCompleteOutboundUrls()
    {
        var services = new ServiceCollection();
        services.AddTelemetryServices(new ConfigurationBuilder().Build(), "integrios-worker");

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<TracerProvider>();
        HttpClientTraceInstrumentationOptions options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);
        options.EnrichWithHttpRequestMessage.ShouldNotBeNull();

        using var activity = new Activity("outbound");
        activity.SetTag("url.full", "https://secret.example.test/private");
        activity.SetTag("http.url", "https://secret.example.test/private");
        activity.SetTag("server.address", "secret.example.test");
        activity.SetTag("server.port", 443);
        options.EnrichWithHttpRequestMessage(activity, new HttpRequestMessage());

        activity.GetTagItem("url.full").ShouldBeNull();
        activity.GetTagItem("http.url").ShouldBeNull();
        activity.GetTagItem("server.address").ShouldBeNull();
        activity.GetTagItem("server.port").ShouldBeNull();
    }

    [Fact]
    public void TelemetryRegistration_ExcludesOAuthTokenRequestsFromExportedTraces()
    {
        var services = new ServiceCollection();
        services.AddTelemetryServices(new ConfigurationBuilder().Build(), "integrios-worker");

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<TracerProvider>();
        HttpClientTraceInstrumentationOptions options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);
        using var request = new HttpRequestMessage();
        request.Options.Set(new HttpRequestOptionsKey<bool>("Integrios.SuppressHttpTelemetry"), true);

        options.FilterHttpRequestMessage.ShouldNotBeNull();
        options.FilterHttpRequestMessage(request).ShouldBeFalse();
    }

    [Fact]
    public async Task OAuthHttpClient_SuppressesEndpointLogsAndMetricsWithoutDisablingOrdinaryMetrics()
    {
        var exporter = new CollectingMetricExporter();
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        services.AddTelemetryServices(new ConfigurationBuilder().Build(), "integrios-worker");
        services.AddWorkerInfrastructureServices(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=integrios;Username=integrios;Password=integrios"
            })
            .Build());
        services.AddOpenTelemetry().WithMetrics(metrics =>
            metrics.AddReader(new BaseExportingMetricReader(exporter)));

        using ServiceProvider provider = services.BuildServiceProvider();
        MeterProvider meterProvider = provider.GetRequiredService<MeterProvider>();
        using var ordinaryClient = new HttpClient();
        using HttpClient oauthClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("oauth2-token");

        await SendLoopbackRequestAsync(ordinaryClient, "127.0.0.1", "ordinary");
        await SendLoopbackRequestAsync(oauthClient, "localhost", "oauth-canary");
        meterProvider.ForceFlush();

        exporter.TagValues.ShouldContain("127.0.0.1");
        exporter.TagValues.ShouldNotContain(value =>
            value.Contains("localhost", StringComparison.OrdinalIgnoreCase));
        logs.Messages
            .Concat(logs.Entries.SelectMany(entry => entry).Select(scope => scope?.ToString() ?? string.Empty))
            .ShouldNotContain(value =>
                value.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || value.Contains("oauth-canary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TelemetryRegistration_UsesTheServiceResourceContract()
    {
        ResourceAttributes first = BuildResourceAttributes("integrios-admin");
        ResourceAttributes second = BuildResourceAttributes("integrios-admin");

        first.ServiceName.ShouldBe("integrios-admin");
        first.ServiceVersion.ShouldBe(typeof(TelemetryExtensions).Assembly.GetName().Version!.ToString(3));
        first.ServiceInstanceId.ShouldNotBeNullOrWhiteSpace();
        second.ServiceInstanceId.ShouldNotBe(first.ServiceInstanceId);
    }

    [Theory]
    [InlineData("not a URI")]
    [InlineData("ftp://collector")]
    public void TelemetryRegistration_RejectsMalformedStandardOtlpEndpoint(string endpoint)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint
            })
            .Build();

        Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddTelemetryServices(configuration, "integrios-admin"));
    }

    [Fact]
    public void TelemetryRegistration_IgnoresTheRetiredEndpointSetting()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Integrios:Telemetry:OtlpEndpoint"] = "not a URI"
            })
            .Build();

        Should.NotThrow(() =>
            new ServiceCollection().AddTelemetryServices(configuration, "integrios-admin"));
    }

    [Fact]
    public void TelemetryRegistration_HonorsStandardResourceAttributes()
    {
        const string key = "OTEL_RESOURCE_ATTRIBUTES";
        string? original = Environment.GetEnvironmentVariable(key);

        try
        {
            Environment.SetEnvironmentVariable(key, "deployment.environment.name=acceptance");

            Resource resource = BuildResource("integrios-admin");

            resource.Attributes.Single(attribute => attribute.Key == "deployment.environment.name").Value
                .ShouldBe("acceptance");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }

    private static ResourceAttributes BuildResourceAttributes(string serviceName)
    {
        Resource resource = BuildResource(serviceName);

        return new ResourceAttributes(
            resource.Attributes.Single(attribute => attribute.Key == "service.name").Value?.ToString(),
            resource.Attributes.Single(attribute => attribute.Key == "service.version").Value?.ToString(),
            resource.Attributes.Single(attribute => attribute.Key == "service.instance.id").Value?.ToString());
    }

    private static Resource BuildResource(string serviceName)
    {
        var services = new ServiceCollection();
        services.AddTelemetryServices(new ConfigurationBuilder().Build(), serviceName);

        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<TracerProvider>().GetResource();
    }

    private static async Task SendLoopbackRequestAsync(HttpClient client, string host, string path)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            Task<HttpResponseMessage> request = client.GetAsync($"http://{host}:{port}/{path}");
            using TcpClient accepted = await accept;
            await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            using HttpResponseMessage response = await request;
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed record ResourceAttributes(string? ServiceName, string? ServiceVersion, string? ServiceInstanceId);

    private sealed class CollectingMetricExporter : BaseExporter<Metric>
    {
        public List<string> TagValues { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                foreach (MetricPoint point in metric.GetMetricPoints())
                    foreach (KeyValuePair<string, object?> tag in point.Tags)
                        TagValues.Add(tag.Value?.ToString() ?? string.Empty);
            }

            return ExportResult.Success;
        }
    }
}
