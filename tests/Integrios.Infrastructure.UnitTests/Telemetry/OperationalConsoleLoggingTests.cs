using Integrios.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
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
            provider.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>().Value.IncludeScopes
                .ShouldBeTrue();
        }
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

    private sealed record ResourceAttributes(string? ServiceName, string? ServiceVersion, string? ServiceInstanceId);
}
