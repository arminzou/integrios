using System.Text.Json;
using Integrios.Application.Connections;
using Integrios.Application.Subscriptions;
using Integrios.Domain.Subscriptions;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Data;
using Integrios.Tests.Shared;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Integrios.Application.FunctionalTests.Admin;

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
                HttpDeliveryConfiguration.Default,
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

    [Fact]
    public async Task ChangeDestinationAuthentication_RejectsActiveStaticHeaderCollisionButIgnoresDisabledSubscriptions()
    {
        var (_, connectionId, topicId) = await SeedGraphAsync();
        await InsertSubscriptionAsync(connectionId, topicId, "header-owner", "active");

        using IServiceScope scope = fixture.WebFactory.Services.CreateScope();
        IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var command = ChangeToApiKeyHeader(connectionId);

        ConnectionValidationException exception = await Assert.ThrowsAsync<ConnectionValidationException>(
            () => mediator.Send(command));
        Assert.Contains("X-Api-Key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("bearer_token", await GetDestinationAuthenticationSchemeAsync(connectionId));

        await SetSubscriptionStatusAsync("header-owner", "disabled");
        ConnectionDto? updated = await mediator.Send(command);

        Assert.NotNull(updated);
        Assert.Equal("api_key_header", updated.DestinationAuthentication?.Scheme);
    }

    [Fact]
    public async Task ChangeDestinationAuthenticationRacingStaticHeaderSubscriptionCreate_PreservesHeaderOwnership()
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
        var conflictingRequest = HttpDeliveryConfiguration.Default with
        {
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "static-value" }
        };

        Task<CommandOutcome> update = CaptureAsync(async () => await updateMediator.Send(
            ChangeToApiKeyHeader(connectionId),
            timeout.Token));
        Task<CommandOutcome> create = CaptureAsync(async () => await createMediator.Send(
            new CreateSubscriptionCommand(
                fixture.TenantId,
                topicId,
                "race-header-subscription",
                Json("""{"event_type":"race.header"}"""),
                connectionId,
                null,
                conflictingRequest,
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
        await using var invariant = new NpgsqlCommand(
            """
            SELECT NOT (
                c.destination_authentication->>'scheme' = 'api_key_header'
                AND c.destination_authentication->'config'->>'header_name' = 'X-Api-Key'
                AND EXISTS (
                    SELECT 1
                    FROM subscriptions s
                    WHERE s.tenant_id = @TenantId
                      AND s.destination_connection_id = c.id
                      AND s.status = 'active'
                      AND s.http_delivery->'headers' ? 'X-Api-Key'))
            FROM connections c
            WHERE c.tenant_id = @TenantId AND c.id = @ConnectionId
            """,
            connection);
        invariant.Parameters.AddWithValue("TenantId", fixture.TenantId);
        invariant.Parameters.AddWithValue("ConnectionId", connectionId);
        Assert.True((bool)(await invariant.ExecuteScalarAsync(timeout.Token))!);
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
                '["api_key_header","bearer_token"]'::jsonb, 'active', @Manifest::jsonb);

            INSERT INTO connections (
                id, tenant_id, integration_id, name, config,
                source_verification, destination_authentication, status)
            VALUES (
                @ConnectionId, @TenantId, @IntegrationId, 'race-destination',
                '{"base_uri":"https://example.test/race"}'::jsonb, NULL,
                '{"scheme":"bearer_token","config":{},"secret_refs":{"token":"race_bearer_token"}}'::jsonb,
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
            ["api_key_header", "bearer_token"]));
        await command.ExecuteNonQueryAsync();
        return (integrationId, connectionId, topicId);
    }

    private async Task InsertSubscriptionAsync(Guid connectionId, Guid topicId, string name, string status)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO subscriptions (
                id, tenant_id, topic_id, name, match_rules, destination_connection_id,
                http_delivery, status)
            VALUES (
                gen_random_uuid(), @TenantId, @TopicId, @Name, '{"event_type":"race.header"}'::jsonb,
                @ConnectionId, '{"version":1,"method":"POST","headers":{"X-Api-Key":"static-value"},"body":"json"}'::jsonb,
                @Status)
            """,
            connection);
        command.Parameters.AddWithValue("TenantId", fixture.TenantId);
        command.Parameters.AddWithValue("TopicId", topicId);
        command.Parameters.AddWithValue("Name", name);
        command.Parameters.AddWithValue("ConnectionId", connectionId);
        command.Parameters.AddWithValue("Status", status);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetSubscriptionStatusAsync(string name, string status)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE subscriptions SET status = @Status WHERE tenant_id = @TenantId AND name = @Name",
            connection);
        command.Parameters.AddWithValue("TenantId", fixture.TenantId);
        command.Parameters.AddWithValue("Name", name);
        command.Parameters.AddWithValue("Status", status);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> GetDestinationAuthenticationSchemeAsync(Guid connectionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT destination_authentication->>'scheme' FROM connections WHERE tenant_id = @TenantId AND id = @ConnectionId",
            connection);
        command.Parameters.AddWithValue("TenantId", fixture.TenantId);
        command.Parameters.AddWithValue("ConnectionId", connectionId);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The Connection does not exist."));
    }

    private UpdateConnectionCommand ChangeToApiKeyHeader(Guid connectionId) => new(
        fixture.TenantId,
        connectionId,
        "race-destination",
        Json("""{"base_uri":"https://example.test/race"}"""),
        null,
        new ConnectionSchemeSelectionInput
        {
            Scheme = "api_key_header",
            Config = Json("""{"header_name":"X-Api-Key"}"""),
            SecretRefs = Json("""{"api_key":"race_api_key"}""")
        },
        null,
        null);

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
