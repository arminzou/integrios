using System.Data.Common;
using Integrios.Application.Telemetry;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Integrios.Tests.Shared;

namespace Integrios.Infrastructure.UnitTests;

public sealed class OutboxDepthMetricsTests
{
    [Fact]
    public void ObservableCollection_DoesNotOpenDatabaseConnection()
    {
        var connectionFactory = new CountingConnectionFactory();
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<IDbConnectionFactory>(connectionFactory);
        services.AddOutboxDepthMetricsServices(configuration);

        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().ShouldHaveSingleItem();

        metrics.CollectObservableInstruments();

        connectionFactory.OpenCount.ShouldBe(0);
        metrics.ForInstrument("integrios_outbox_pending_depth").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00:00:00")]
    public void Registration_RejectsInvalidSampleInterval(string interval)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Integrios:Telemetry:OutboxDepthSampleInterval"] = interval
            })
            .Build();

        Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddOutboxDepthMetricsServices(configuration));
    }

    private sealed class CountingConnectionFactory : IDbConnectionFactory
    {
        public DatabaseProvider Provider => DatabaseProvider.Postgres;

        public int OpenCount { get; private set; }

        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return ValueTask.FromException<DbConnection>(new InvalidOperationException("Database access was not expected."));
        }
    }
}
