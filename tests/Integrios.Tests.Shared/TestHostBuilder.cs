using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.Tests.Shared;

/// Runs one role in process from the role's own builder and pipeline, as its RunAsync does. The
/// executable picks the role in Main, so WebApplicationFactory, which can only run Main, cannot.
///
/// Settings reach the role as command-line arguments, so composition reads them exactly as it reads
/// a deployment's; service overrides apply after the role's own registrations. Each With call
/// returns a new, unstarted host, and a host starts on first use.
public sealed class TestHostBuilder : IDisposable
{
    private readonly Func<string[], (WebApplicationBuilder Builder, int OperationalPort)> createBuilder;
    private readonly Action<WebApplication, int> configurePipeline;
    private readonly KeyValuePair<string, string?>[] settings;
    private readonly Action<IServiceCollection>[] serviceOverrides;
    private readonly int? kestrelPort;
    private readonly Lazy<WebApplication> app;

    public TestHostBuilder(
        Func<string[], (WebApplicationBuilder Builder, int OperationalPort)> createBuilder,
        Action<WebApplication, int> configurePipeline)
        : this(createBuilder, configurePipeline, [], [], null)
    {
    }

    private TestHostBuilder(
        Func<string[], (WebApplicationBuilder Builder, int OperationalPort)> createBuilder,
        Action<WebApplication, int> configurePipeline,
        KeyValuePair<string, string?>[] settings,
        Action<IServiceCollection>[] serviceOverrides,
        int? kestrelPort)
    {
        this.createBuilder = createBuilder;
        this.configurePipeline = configurePipeline;
        this.settings = settings;
        this.serviceOverrides = serviceOverrides;
        this.kestrelPort = kestrelPort;
        app = new Lazy<WebApplication>(Start);
    }

    public IServiceProvider Services => app.Value.Services;

    public TestHostBuilder WithSetting(string key, string? value) =>
        new(createBuilder, configurePipeline, [.. settings, new(key, value)], serviceOverrides, kestrelPort);

    public TestHostBuilder WithSettings(IConfiguration configuration) =>
        new(createBuilder, configurePipeline,
            [.. settings, .. configuration.AsEnumerable().Where(setting => setting.Value is not null)],
            serviceOverrides, kestrelPort);

    public TestHostBuilder WithServices(Action<IServiceCollection> configure) =>
        new(createBuilder, configurePipeline, settings, [.. serviceOverrides, configure], kestrelPort);

    /// Listens on a real loopback socket for the product port instead of serving in memory.
    public TestHostBuilder WithKestrel(int productPort) =>
        new(createBuilder, configurePipeline, settings, serviceOverrides, productPort);

    public void StartServer() => _ = app.Value;

    public HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

    public HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        HttpMessageHandler handler = ((TestServer)Services.GetRequiredService<IServer>()).CreateHandler();
        if (options.HandleCookies)
            handler = new CookieContainerHandler { InnerHandler = handler };
        if (options.AllowAutoRedirect)
            handler = new RedirectHandler(options.MaxAutomaticRedirections) { InnerHandler = handler };
        return new HttpClient(handler) { BaseAddress = options.BaseAddress };
    }

    public void Dispose()
    {
        if (!app.IsValueCreated)
            return;
        app.Value.StopAsync().GetAwaiter().GetResult();
        ((IDisposable)app.Value).Dispose();
    }

    private WebApplication Start()
    {
        string[] args =
        [
            "--environment=Development",
            $"--contentRoot={AppContext.BaseDirectory}",
            .. kestrelPort is int port ? [$"--urls=http://127.0.0.1:{port}"] : Array.Empty<string>(),
            .. settings.Select(setting => $"--{setting.Key}={setting.Value}"),
        ];
        (WebApplicationBuilder builder, int operationalPort) = createBuilder(args);
        if (kestrelPort is null)
            builder.WebHost.UseTestServer();
        foreach (Action<IServiceCollection> configure in serviceOverrides)
            configure(builder.Services);

        WebApplication built = builder.Build();
        configurePipeline(built, operationalPort);
        built.Start();
        return built;
    }
}
