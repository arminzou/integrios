using Integrios.Worker;
using Npgsql;

namespace Integrios.IntegrationTests;

public sealed class ReplayTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public ReplayTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Replay_DeadLetteredEvent_ResetsStatusAndEnqueuesNewOutboxRow()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < OutboxWorker.MaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync();
        Assert.Equal("dead_lettered", await fixture.GetEventStatusAsync(eventId));

        var replayed = await fixture.ReplayAsync(eventId);

        Assert.True(replayed);
        Assert.Equal("accepted", await fixture.GetEventStatusAsync(eventId));
        Assert.Equal(2, await GetOutboxRowCountAsync(eventId));
    }

    [Fact]
    public async Task Replay_AcceptedEvent_ReturnsFalse()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        var replayed = await fixture.ReplayAsync(eventId);

        Assert.False(replayed);
    }

    [Fact]
    public async Task Replay_ReplayedEventIsPickedUpByWorker()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < OutboxWorker.MaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync();

        await fixture.ReplayAsync(eventId);

        fixture.DeliveryClient.ShouldSucceed = true;
        fixture.DeliveryClient.Reset();

        var processed = await fixture.RunWorkerBatchAsync();
        Assert.Equal(1, processed);
        Assert.Single(fixture.DeliveryClient.Calls);
        Assert.Equal("completed", await fixture.GetEventStatusAsync(eventId));
    }

    private async Task<int> GetOutboxRowCountAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM outbox WHERE event_id = @EventId", connection);
        cmd.Parameters.AddWithValue("EventId", eventId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
