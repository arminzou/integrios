using System.Text.Json;
using Dapper;
using Integrios.Application.Connections;
using Integrios.Application.Subscriptions;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Connections;
using Integrios.Tests.Shared;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        IConnectionAuthoringLock innerLock = fixture.WebFactory.Services.GetRequiredService<IConnectionAuthoringLock>();
        await using WebApplicationFactory<Program> factory = fixture.WebFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConnectionAuthoringLock>();
                services.AddSingleton<IConnectionAuthoringLock>(
                    new CoordinatingConnectionAuthoringLock(innerLock, connectionId));
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

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(timeout.Token);
        dynamic invariant = await connection.QuerySingleAsync($$$"""
            SELECT
                (SELECT COUNT(*) FROM subscriptions WHERE destination_connection_id = @ConnectionId AND status = 'active') AS ActiveSubscriptions,
                {{{fixture.JsonText("destination_authentication")}}} AS DestinationAuthentication
            FROM connections
            WHERE id = @ConnectionId
            """, new { ConnectionId = connectionId });
        Assert.Equal(Convert.ToInt32(invariant.ActiveSubscriptions) > 0,
            invariant.DestinationAuthentication is string);
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
        IConnectionAuthoringLock innerLock = fixture.WebFactory.Services.GetRequiredService<IConnectionAuthoringLock>();
        await using WebApplicationFactory<Program> factory = fixture.WebFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConnectionAuthoringLock>();
                services.AddSingleton<IConnectionAuthoringLock>(
                    new CoordinatingConnectionAuthoringLock(innerLock, connectionId));
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

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(timeout.Token);
        string authentication = await connection.ExecuteScalarAsync<string>($$$"""
            SELECT {{{fixture.JsonText("destination_authentication")}}}
            FROM connections WHERE tenant_id=@TenantId AND id=@ConnectionId
            """, new { fixture.TenantId, ConnectionId = connectionId })
            ?? throw new InvalidOperationException("The Connection does not exist.");
        IEnumerable<string> deliveries = await connection.QueryAsync<string>($$$"""
            SELECT {{{fixture.JsonText("http_delivery")}}}
            FROM subscriptions
            WHERE tenant_id=@TenantId AND destination_connection_id=@ConnectionId AND status='active'
            """, new { fixture.TenantId, ConnectionId = connectionId });
        JsonElement auth = Json(authentication);
        bool headerOwned = auth.GetProperty("scheme").GetString() == "api_key_header"
            && auth.GetProperty("config").GetProperty("header_name").GetString() == "X-Api-Key";
        bool staticCollision = deliveries.Select(Json).Any(delivery =>
            delivery.GetProperty("headers").TryGetProperty("X-Api-Key", out _));
        Assert.False(headerOwned && staticCollision);
    }

    private async Task<(Guid ConnectorId, Guid ConnectionId, Guid TopicId)> SeedGraphAsync()
    {
        Guid connectorId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid topicId = Guid.NewGuid();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO connectors (
                id, {{{fixture.KeyColumn}}}, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest)
            VALUES (
                @ConnectorId, 'race_destination', 1, 1, 'Race Destination', 'destination',
                {{{fixture.Json("@Schemes")}}}, 'active', {{{fixture.Json("@Manifest")}}});

            INSERT INTO connections (
                id, tenant_id, connector_id, name, config,
                source_verification, destination_authentication, status)
            VALUES (
                @ConnectionId, @TenantId, @ConnectorId, 'race-destination',
                {{{fixture.Json("@Config")}}}, NULL,
                {{{fixture.Json("@Authentication")}}},
                'active');

            INSERT INTO topics (id, tenant_id, name, status)
            VALUES (@TopicId, @TenantId, 'race-topic', 'active');
            """, new
        {
            ConnectorId = connectorId,
            ConnectionId = connectionId,
            TopicId = topicId,
            fixture.TenantId,
            Schemes = "[\"api_key_header\",\"bearer_token\"]",
            Config = "{\"base_uri\":\"https://example.test/race\"}",
            Authentication = "{\"scheme\":\"bearer_token\",\"config\":{},\"secret_refs\":{\"token\":\"race_bearer_token\"}}",
            Manifest = TestConnectorManifest.Create("race_destination", "Race Destination", "destination",
                ["api_key_header", "bearer_token"])
        });
        return (connectorId, connectionId, topicId);
    }

    private async Task InsertSubscriptionAsync(Guid connectionId, Guid topicId, string name, string status)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO subscriptions (
                id, tenant_id, topic_id, name, match_rules, destination_connection_id,
                http_delivery, status)
            VALUES (
                @Id, @TenantId, @TopicId, @Name, {{{fixture.Json("@MatchRules")}}},
                @ConnectionId, {{{fixture.Json("@HttpDelivery")}}},
                @Status)
            """, new
        {
            Id = Guid.NewGuid(), fixture.TenantId, TopicId = topicId, Name = name,
            MatchRules = "{\"event_type\":\"race.header\"}", ConnectionId = connectionId,
            HttpDelivery = "{\"version\":1,\"method\":\"POST\",\"headers\":{\"X-Api-Key\":\"static-value\"},\"body\":\"json\"}",
            Status = status
        });
    }

    private async Task SetSubscriptionStatusAsync(string name, string status)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE subscriptions SET status = @Status WHERE tenant_id = @TenantId AND name = @Name",
            new { fixture.TenantId, Name = name, Status = status });
    }

    private async Task<string> GetDestinationAuthenticationSchemeAsync(Guid connectionId)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        string authentication = await connection.ExecuteScalarAsync<string>($$$"""
            SELECT {{{fixture.JsonText("destination_authentication")}}}
            FROM connections WHERE tenant_id=@TenantId AND id=@ConnectionId
            """, new { fixture.TenantId, ConnectionId = connectionId })
            ?? throw new InvalidOperationException("The Connection does not exist.");
        return Json(authentication).GetProperty("scheme").GetString()
            ?? throw new InvalidOperationException("The Connection does not exist.");
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
