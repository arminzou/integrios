using Integrios.Application.Delivery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Server;

namespace Integrios.FunctionalTests.Worker;

/// The Worker routing fixture with the production HTTP delivery client, sending to an in-process
/// WireMock server instead of the fake client, so what a Destination receives on the wire is observable.
public sealed class HttpSinkFixture : WorkerRoutingFixture
{
    // Short enough that a stubbed delay outlasts it without the suite waiting the production timeout.
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(2);

    public WireMockServer Sink { get; private set; } = null!;

    public string SinkUrl(string path) => $"{Sink.Url}/{path.TrimStart('/')}";

    public override async Task InitializeAsync()
    {
        Sink = WireMockServer.Start();
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        Sink.Stop();
        Sink.Dispose();
    }

    public override async Task ResetAsync()
    {
        Sink.Reset();
        await base.ResetAsync();
    }

    protected override IConfiguration WorkerConfiguration(IConfiguration databaseConfiguration) =>
        new ConfigurationBuilder()
            .AddConfiguration(databaseConfiguration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Integrios:Delivery:HttpTimeout"] = HttpTimeout.ToString(),
            })
            .Build();

    protected override IDeliveryClient ResolveDeliveryClient(IServiceProvider infrastructure) =>
        infrastructure.GetRequiredService<IDeliveryClient>();
}
