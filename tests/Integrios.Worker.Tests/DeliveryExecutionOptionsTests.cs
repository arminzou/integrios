using System.Net;
using System.Net.Sockets;
using System.Text;
using Integrios.Application.Delivery;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Integrios.Worker.Tests;

public sealed class DeliveryExecutionOptionsTests
{
    [Fact]
    public void Defaults_MatchFencedLeaseTimingContract()
    {
        DeliveryExecutionOptions options = DeliveryExecutionOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(30), options.HttpTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), options.AttemptDeadline);
        Assert.Equal(TimeSpan.FromMinutes(2), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ShutdownGracePeriod);
        Assert.Equal(TimeSpan.FromSeconds(2), options.IdlePollInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryBaseDelay);
        Assert.Equal(3, options.RetryMaxAttempts);
        options.Validate();
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Validate_RejectsUnsafeTimingRelationships(DeliveryExecutionOptions options, string expectedSetting)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(expectedSetting, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<DeliveryExecutionOptions, string> InvalidOptions => new()
    {
        { new(TimeSpan.Zero, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60)), "HttpTimeout" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60)), "AttemptDeadline" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60)), "LeaseDuration" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(45)), "ShutdownGracePeriod" },
        { Valid with { IdlePollInterval = TimeSpan.Zero }, "IdlePollInterval" },
        { Valid with { RetryBaseDelay = TimeSpan.Zero }, "Retry:BaseDelay" },
        { Valid with { RetryMaxAttempts = 0 }, "Retry:MaxAttempts" }
    };

    private static DeliveryExecutionOptions Valid => DeliveryExecutionOptions.Default;

    [Fact]
    public void InfrastructureRegistration_AppliesConfiguredRetryCadence()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:IdlePollInterval"] = "00:00:00.250",
            ["Integrios:Delivery:Retry:BaseDelay"] = "00:00:02",
            ["Integrios:Delivery:Retry:MaxAttempts"] = "5"
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegriosApplication();
        services.AddIntegriosInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        DeliveryExecutionOptions options = provider.GetRequiredService<DeliveryExecutionOptions>();
        RetryPolicy policy = provider.GetRequiredService<RetryPolicy>();

        Assert.Equal(TimeSpan.FromMilliseconds(250), options.IdlePollInterval);
        // The configured policy must win over the default Application registration.
        Assert.Equal(TimeSpan.FromSeconds(2), policy.BaseDelay);
        Assert.Equal(5, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(4), policy.CalculateBackoff(2));
    }

    [Fact]
    public void InfrastructureRegistration_NonIntegerMaxAttempts_FailsStartupRegistration()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:Retry:MaxAttempts"] = "many"
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddIntegriosInfrastructure(configuration));

        Assert.Contains("Retry:MaxAttempts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastructureRegistration_AppliesConfiguredExecutionTimings()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:HttpTimeout"] = "00:00:11",
            ["Integrios:Delivery:AttemptDeadline"] = "00:00:22",
            ["Integrios:Delivery:LeaseDuration"] = "00:00:44",
            ["Integrios:Delivery:ShutdownGracePeriod"] = "00:00:33"
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegriosApplication();
        services.AddIntegriosInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        DeliveryExecutionOptions options = provider.GetRequiredService<DeliveryExecutionOptions>();
        HostOptions hostOptions = provider.GetRequiredService<IOptions<HostOptions>>().Value;
        HttpClient deliveryHttpClient = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IDeliveryClient));

        Assert.Equal(TimeSpan.FromSeconds(11), options.HttpTimeout);
        Assert.Equal(TimeSpan.FromSeconds(22), options.AttemptDeadline);
        Assert.Equal(TimeSpan.FromSeconds(44), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(33), options.ShutdownGracePeriod);
        Assert.Equal(options.HttpTimeout, deliveryHttpClient.Timeout);
        // Shared infrastructure must not couple host shutdown to delivery settings;
        // only the Worker wires ShutdownGracePeriod into HostOptions.
        Assert.Equal(new HostOptions().ShutdownTimeout, hostOptions.ShutdownTimeout);
    }

    [Fact]
    public void InfrastructureRegistration_InvalidConfiguredRelationship_FailsStartupRegistration()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:HttpTimeout"] = "00:00:30",
            ["Integrios:Delivery:AttemptDeadline"] = "00:00:20"
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddIntegriosInfrastructure(configuration));

        Assert.Contains("AttemptDeadline", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InfrastructureRegistration_DeliveryClientDoesNotFollowRedirects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string destination = $"http://127.0.0.1:{port}/initial";
        string redirectTarget = $"http://127.0.0.1:{port}/redirected";
        using var stopServer = new CancellationTokenSource();
        Task<RedirectObservation> serverTask = ObserveRedirectAsync(listener, redirectTarget, stopServer.Token);

        IConfiguration configuration = BuildConfiguration([]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegriosApplication();
        services.AddIntegriosInfrastructure(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IDeliveryClient deliveryClient = provider.GetRequiredService<IDeliveryClient>();

        DeliveryResult result = await deliveryClient.DeliverAsync(
            destination,
            "{}",
            request => request.Headers.TryAddWithoutValidation("X-Api-Key", "must-not-follow"));

        await stopServer.CancelAsync();
        RedirectObservation observation = await serverTask;

        Assert.False(result.Succeeded);
        Assert.Equal((int)HttpStatusCode.Found, result.StatusCode);
        Assert.Equal(1, observation.RequestCount);
        Assert.Contains("X-Api-Key: must-not-follow", observation.FirstRequest, StringComparison.OrdinalIgnoreCase);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios;Username=integrios;Password=integrios";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<RedirectObservation> ObserveRedirectAsync(
        TcpListener listener,
        string redirectTarget,
        CancellationToken cancellationToken)
    {
        int requestCount = 0;
        string firstRequest;

        using (TcpClient firstClient = await listener.AcceptTcpClientAsync(cancellationToken))
        {
            requestCount++;
            firstRequest = await ReadHeadersAsync(firstClient.GetStream(), cancellationToken);
            await WriteResponseAsync(
                firstClient.GetStream(),
                $"HTTP/1.1 302 Found\r\nLocation: {redirectTarget}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                cancellationToken);
        }

        try
        {
            using TcpClient redirectedClient = await listener.AcceptTcpClientAsync(cancellationToken);
            requestCount++;
            await ReadHeadersAsync(redirectedClient.GetStream(), cancellationToken);
            await WriteResponseAsync(
                redirectedClient.GetStream(),
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return new RedirectObservation(requestCount, firstRequest);
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var request = new StringBuilder();

        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return request.ToString();
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string response, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private sealed record RedirectObservation(int RequestCount, string FirstRequest);
}
