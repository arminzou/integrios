using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
