using System.Data.Common;
using System.Diagnostics.Metrics;
using Integrios.Application.Telemetry;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        metrics.ForInstrument("integrios_outbox_oldest_pending_age_seconds").ShouldBeEmpty();
        metrics.ForInstrument("integrios_delivery_ready_depth").ShouldBeEmpty();
        metrics.ForInstrument("integrios_delivery_oldest_ready_age_seconds").ShouldBeEmpty();
        metrics.ForInstrument("integrios_backlog_snapshot_age_seconds").ShouldBeEmpty();
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

    [Fact]
    public async Task SamplerWarning_DoesNotCarryTheProviderException()
    {
        var logs = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        using ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var sampler = new OutboxDepthMetrics(
            services.GetRequiredService<IMeterFactory>(),
            new BacklogSnapshotReader(new FailingConnectionFactory()),
            new OutboxDepthMetricsOptions(TimeSpan.FromSeconds(1)),
            loggerFactory.CreateLogger<OutboxDepthMetrics>());

        await sampler.SampleAsync(CancellationToken.None);

        CapturedLogRecord record = logs.Records.ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Exception.ShouldBeNull();
        record.Message.ShouldNotContain(FailingConnectionFactory.ProviderMessage);
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

    private sealed class FailingConnectionFactory : IDbConnectionFactory
    {
        public const string ProviderMessage = "provider secret";

        public DatabaseProvider Provider => DatabaseProvider.Postgres;

        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DbConnection>(new InvalidOperationException(ProviderMessage));
    }
}
