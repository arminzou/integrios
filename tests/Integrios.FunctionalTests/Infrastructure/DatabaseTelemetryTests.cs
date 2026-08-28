using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Integrios.Application.Telemetry;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Telemetry;
using Integrios.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Integrios.FunctionalTests.Infrastructure;

public sealed class DatabaseTelemetryFixture : IAsyncLifetime
{
    internal FunctionalDatabase Database { get; } = new();

    public Task InitializeAsync() => Database.StartAsync();

    public async Task DisposeAsync() => await Database.DisposeAsync();
}

public sealed class DatabaseTelemetryTests(DatabaseTelemetryFixture fixture)
    : IClassFixture<DatabaseTelemetryFixture>
{
    [Fact]
    public async Task ExecuteCommand_WithConfiguredDatabaseTelemetry_EmitsClientActivity()
    {
        var recorder = new RecordingActivityProcessor();
        var services = new ServiceCollection();
        services.AddAdminInfrastructureServices(fixture.Database.Configuration);
        services.AddTelemetryServices(fixture.Database.Configuration, "integrios-telemetry-tests");
        services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(recorder));

        await using ServiceProvider provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();

        IDbConnectionFactory connectionFactory = provider.GetRequiredService<IDbConnectionFactory>();
        await using (DbConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None))
        {
            recorder.Clear();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync();
            recorder.Completed.ShouldContain(activity => activity.Kind == ActivityKind.Client);
        }

        IDbContextFactory<IntegriosDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<IntegriosDbContext>>();
        await using IntegriosDbContext context = await contextFactory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        recorder.Clear();
        await context.Database.ExecuteSqlRawAsync("SELECT 1");
        recorder.Completed.ShouldContain(activity => activity.Kind == ActivityKind.Client);
    }

    [Fact]
    public async Task BacklogSampler_CachesSnapshotAcrossScrapesAndFailures()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddWorkerInfrastructureServices(fixture.Database.Configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var connectionFactory = new CountingConnectionFactory(
            provider.GetRequiredService<IDbConnectionFactory>());
        var sampler = new OutboxDepthMetrics(
            provider.GetRequiredService<IMeterFactory>(),
            new BacklogSnapshotReader(connectionFactory),
            new OutboxDepthMetricsOptions(TimeSpan.FromSeconds(1)),
            NullLogger<OutboxDepthMetrics>.Instance);

        await sampler.SampleAsync(CancellationToken.None);
        connectionFactory.OpenCount.ShouldBe(1);
        metrics.CollectObservableInstruments();

        metrics.ForInstrument("integrios_outbox_pending_depth").ShouldHaveSingleItem().Value.ShouldBe(0);
        metrics.ForInstrument("integrios_outbox_oldest_pending_age_seconds").ShouldHaveSingleItem().Value.ShouldBe(0);
        metrics.ForInstrument("integrios_delivery_ready_depth").ShouldHaveSingleItem().Value.ShouldBe(0);
        metrics.ForInstrument("integrios_delivery_oldest_ready_age_seconds").ShouldHaveSingleItem().Value.ShouldBe(0);
        double firstSnapshotAge = metrics.ForInstrument("integrios_backlog_snapshot_age_seconds")
            .ShouldHaveSingleItem().Value;
        connectionFactory.OpenCount.ShouldBe(1);

        connectionFactory.Fail = true;
        await Task.Delay(20);
        await sampler.SampleAsync(CancellationToken.None);
        metrics.CollectObservableInstruments();

        connectionFactory.OpenCount.ShouldBe(2);
        metrics.ForInstrument("integrios_outbox_pending_depth").Last().Value.ShouldBe(0);
        metrics.ForInstrument("integrios_outbox_oldest_pending_age_seconds").Last().Value.ShouldBe(0);
        metrics.ForInstrument("integrios_delivery_ready_depth").Last().Value.ShouldBe(0);
        metrics.ForInstrument("integrios_delivery_oldest_ready_age_seconds").Last().Value.ShouldBe(0);
        metrics.ForInstrument("integrios_backlog_snapshot_age_seconds").Last().Value
            .ShouldBeGreaterThan(firstSnapshotAge);
    }

    private sealed class CountingConnectionFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public DatabaseProvider Provider => inner.Provider;
        public int OpenCount { get; private set; }
        public bool Fail { get; set; }

        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return Fail
                ? ValueTask.FromException<DbConnection>(new InvalidOperationException("Injected sample failure."))
                : inner.OpenConnectionAsync(cancellationToken);
        }
    }

    private sealed class RecordingActivityProcessor : BaseProcessor<Activity>
    {
        private readonly ConcurrentQueue<Activity> completed = new();

        public IReadOnlyCollection<Activity> Completed => completed;

        public override void OnEnd(Activity data) => completed.Enqueue(data);

        public void Clear()
        {
            while (completed.TryDequeue(out _))
            {
            }
        }
    }
}
