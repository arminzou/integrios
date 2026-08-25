using System.Net;
using System.Net.Sockets;
using System.Text;
using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Integrios.Infrastructure.UnitTests;

public sealed class WorkerInfrastructureRegistrationTests
{
    [Fact]
    public void WorkerInfrastructureRegistration_AppliesConfiguredRetryPolicy()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:Retry:BaseDelay"] = "00:00:02",
            ["Integrios:Delivery:Retry:MaxAttempts"] = "5"
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkerApplicationServices();
        services.AddWorkerInfrastructureServices(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        DeliveryExecutionOptions options = provider.GetRequiredService<DeliveryExecutionOptions>();
        RetryPolicy policy = provider.GetRequiredService<RetryPolicy>();

        // Worker configuration is the only delivery-policy registration.
        policy.BaseDelay.ShouldBe(TimeSpan.FromSeconds(2));
        policy.MaxAttempts.ShouldBe(5);
        policy.CalculateBackoff(2).ShouldBe(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void WorkerInfrastructureRegistration_NonIntegerMaxAttempts_FailsStartupRegistration()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:Retry:MaxAttempts"] = "many"
        });

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddWorkerInfrastructureServices(configuration));

        exception.Message.ShouldContain("Retry:MaxAttempts", Case.Sensitive);
    }

    [Fact]
    public void WorkerInfrastructureRegistration_AppliesConfiguredExecutionTimings()
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
        services.AddWorkerApplicationServices();
        services.AddWorkerInfrastructureServices(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        DeliveryExecutionOptions options = provider.GetRequiredService<DeliveryExecutionOptions>();
        HostOptions hostOptions = provider.GetRequiredService<IOptions<HostOptions>>().Value;
        HttpClient deliveryHttpClient = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IDeliveryClient));

        options.HttpTimeout.ShouldBe(TimeSpan.FromSeconds(11));
        options.AttemptDeadline.ShouldBe(TimeSpan.FromSeconds(22));
        options.LeaseDuration.ShouldBe(TimeSpan.FromSeconds(44));
        options.ShutdownGracePeriod.ShouldBe(TimeSpan.FromSeconds(33));
        deliveryHttpClient.Timeout.ShouldBe(options.HttpTimeout);
        // The Worker composition root, not its Infrastructure module, owns shutdown behavior.
        hostOptions.ShutdownTimeout.ShouldBe(new HostOptions().ShutdownTimeout);
    }

    [Fact]
    public void WorkerInfrastructureRegistration_InvalidConfiguredRelationship_FailsStartupRegistration()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:HttpTimeout"] = "00:00:30",
            ["Integrios:Delivery:AttemptDeadline"] = "00:00:20"
        });

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddWorkerInfrastructureServices(configuration));

        exception.Message.ShouldContain("AttemptDeadline", Case.Sensitive);
    }

    [Fact]
    public async Task WorkerInfrastructureRegistration_DeliveryClientDoesNotFollowRedirects()
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
        services.AddWorkerApplicationServices();
        services.AddWorkerInfrastructureServices(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IDeliveryClient deliveryClient = provider.GetRequiredService<IDeliveryClient>();

        var request = new OutboundHttpMessage(
            "POST",
            destination,
            new Dictionary<string, string> { ["X-Api-Key"] = "must-not-follow" },
            "{}");
        DeliveryResult result = await deliveryClient.DeliverAsync(request, null, CancellationToken.None);

        await stopServer.CancelAsync();
        RedirectObservation observation = await serverTask;

        result.Succeeded.ShouldBeFalse();
        result.StatusCode.ShouldBe((int)HttpStatusCode.Found);
        observation.RequestCount.ShouldBe(1);
        observation.FirstRequest.ShouldContain("X-Api-Key: must-not-follow", Case.Insensitive);
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
