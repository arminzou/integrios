using System.Text.Json;
using Integrios.Application.Connections;
using Integrios.Application.Subscriptions;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Integrios.Admin.Tests;

public sealed class ConnectionAuthoringCommandRaceTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;

    public ConnectionAuthoringCommandRaceTests(AdminApiFixture fixture) => this.fixture = fixture;

    public Task DisposeAsync() => Task.CompletedTask;

    public Task InitializeAsync() => fixture.ResetAsync();

    [Fact]
    public async Task RemoveDestinationAuthenticationRacingSubscriptionCreate_PreservesUseReadiness()
    {
        var (_, connectionId, topicId) = await SeedGraphAsync();
        await using WebApplicationFactory<Program> factory = fixture.WebFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConnectionAuthoringLock>();
                services.AddSingleton<IConnectionAuthoringLock>(provider =>
                    new CoordinatingConnectionAuthoringLock(
                        new PostgresConnectionAuthoringLock(provider.GetRequiredService<IDbConnectionFactory>()),
                        connectionId));
            });
        });

        using IServiceScope updateScope = factory.Services.CreateScope();
        using IServiceScope createScope = factory.Services.CreateScope();
        IMediator updateMediator = updateScope.ServiceProvider.GetRequiredService<IMediator>();
        IMediator createMediator = createScope.ServiceProvider.GetRequiredService<IMediator>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Task<CommandOutcome> update = CaptureAsync(async () => await updateMediator.Send(
            new UpdateConnectionCommand(
                fixture.TenantId,
                connectionId,
                "race-destination",
                Json("""{"base_uri":"https://example.test/race"}"""),
                null,
                null,
                null,
                null),
            timeout.Token));
        Task<CommandOutcome> create = CaptureAsync(async () => await createMediator.Send(
            new CreateSubscriptionCommand(
                fixture.TenantId,
                topicId,
                "race-subscription",
                Json("""{"event_type":"race.test"}"""),
                connectionId,
                null,
                0,
                null),
            timeout.Token));

        CommandOutcome[] outcomes = await Task.WhenAll(update, create).WaitAsync(timeout.Token);

        Assert.Single(outcomes, outcome => outcome.Succeeded);
        CommandOutcome failure = Assert.Single(outcomes, outcome => !outcome.Succeeded);
        Assert.True(
            failure.Exception is ConnectionValidationException or SubscriptionValidationException,
            failure.Exception?.ToString());

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM subscriptions WHERE destination_connection_id = @ConnectionId AND status = 'active'),
                destination_authentication IS NOT NULL
            FROM connections
            WHERE id = @ConnectionId
            """,
            connection);
        command.Parameters.AddWithValue("ConnectionId", connectionId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token);
        Assert.True(await reader.ReadAsync(timeout.Token));
        Assert.Equal(reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private async Task<(Guid IntegrationId, Guid ConnectionId, Guid TopicId)> SeedGraphAsync()
    {
        Guid integrationId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid topicId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest)
            VALUES (
                @IntegrationId, 'race_destination', 1, 1, 'Race Destination', 'destination',
                '["api_key_header"]'::jsonb, 'active', @Manifest::jsonb);

            INSERT INTO connections (
                id, tenant_id, integration_id, name, config,
                source_verification, destination_authentication, status)
            VALUES (
                @ConnectionId, @TenantId, @IntegrationId, 'race-destination',
                '{"base_uri":"https://example.test/race"}'::jsonb, NULL,
                '{"scheme":"api_key_header","config":{"header_name":"X-Api-Key"},"secret_refs":{"api_key":"race_api_key"}}'::jsonb,
                'active');

            INSERT INTO topics (id, tenant_id, name, status)
            VALUES (@TopicId, @TenantId, 'race-topic', 'active');
            """,
            connection);
        command.Parameters.AddWithValue("IntegrationId", integrationId);
        command.Parameters.AddWithValue("ConnectionId", connectionId);
        command.Parameters.AddWithValue("TopicId", topicId);
        command.Parameters.AddWithValue("TenantId", fixture.TenantId);
        command.Parameters.AddWithValue("Manifest", TestIntegrationManifest.Create(
            "race_destination",
            "Race Destination",
            "destination",
            ["api_key_header"]));
        await command.ExecuteNonQueryAsync();
        return (integrationId, connectionId, topicId);
    }

    private static async Task<CommandOutcome> CaptureAsync<T>(Func<Task<T?>> action)
        where T : class
    {
        try
        {
            T? result = await action();
            return result is not null
                ? new CommandOutcome(true, null)
                : new CommandOutcome(false, new InvalidOperationException("The command returned no result."));
        }
        catch (Exception exception)
        {
            return new CommandOutcome(false, exception);
        }
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private sealed record CommandOutcome(bool Succeeded, Exception? Exception);

    private sealed class CoordinatingConnectionAuthoringLock(
        IConnectionAuthoringLock inner,
        Guid targetConnectionId) : IConnectionAuthoringLock
    {
        private readonly TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public async Task<IAsyncDisposable> AcquireAsync(
            IEnumerable<Guid> connectionIds,
            CancellationToken cancellationToken = default)
        {
            Guid[] ids = connectionIds.ToArray();
            if (ids.Contains(targetConnectionId))
            {
                if (Interlocked.Increment(ref arrivals) == 2)
                    bothArrived.TrySetResult();
                await bothArrived.Task.WaitAsync(cancellationToken);
            }

            return await inner.AcquireAsync(ids, cancellationToken);
        }
    }
}
