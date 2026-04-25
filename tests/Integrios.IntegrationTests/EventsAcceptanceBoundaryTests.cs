using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Integrios.Application.Events;
using Integrios.Domain.Events;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.IntegrationTests;

public sealed class EventsAcceptanceBoundaryTests : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    private readonly PostgresApiFixture fixture;
    private readonly string tenantAAuthHeaderValue;
    private readonly string tenantBAuthHeaderValue;
    private HttpClient client = null!;

    public EventsAcceptanceBoundaryTests(PostgresApiFixture fixture)
    {
        this.fixture = fixture;
        tenantAAuthHeaderValue = $"ApiKey {PostgresApiFixture.TenantAPublicKey}:{PostgresApiFixture.TenantAApiSecret}";
        tenantBAuthHeaderValue = $"ApiKey {PostgresApiFixture.TenantBPublicKey}:{PostgresApiFixture.TenantBApiSecret}";
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PostEvents_PersistsEventAndOutbox()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-1");

        var response = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(body);
        Assert.False(body.IsDuplicate);
        Assert.Equal(EventStatus.Accepted, body.Status);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var eventCountCommand = new NpgsqlCommand("SELECT COUNT(*) FROM events;", connection);
        var eventCount = (long)(await eventCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, eventCount);

        await using var outboxCountCommand = new NpgsqlCommand("SELECT COUNT(*) FROM outbox;", connection);
        var outboxCount = (long)(await outboxCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, outboxCount);
    }

    [Fact]
    public async Task GetEventsById_ReturnsEvent_WhenEventExists()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-read-1");
        var postResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(postBody);

        var getResponse = await GetEventAsync(postBody.EventId);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getBody = await getResponse.Content.ReadFromJsonAsync<GetEventResponse>();
        Assert.NotNull(getBody);
        Assert.Equal(postBody.EventId, getBody.EventId);
        Assert.Equal(EventStatus.Accepted, getBody.Status);
        Assert.NotEqual(default, getBody.AcceptedAt);
    }

    [Fact]
    public async Task GetEventsById_Returns404_WhenEventDoesNotExist()
    {
        var getResponse = await GetEventAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetEventsById_OtherTenant_Returns404()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-tenant-isolation");
        var postResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(postBody);

        var getResponseForTenantB = await GetEventAsync(postBody.EventId, tenantBAuthHeaderValue);
        Assert.Equal(HttpStatusCode.NotFound, getResponseForTenantB.StatusCode);
    }

    [Fact]
    public async Task PostEvents_DuplicateIdempotencyKey_IsSuppressed()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-dup");

        var firstResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(firstBody);
        Assert.False(firstBody.IsDuplicate);

        var secondResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(secondBody);
        Assert.True(secondBody.IsDuplicate);
        Assert.Equal(firstBody.EventId, secondBody.EventId);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var eventCountCommand = new NpgsqlCommand("SELECT COUNT(*) FROM events;", connection);
        var eventCount = (long)(await eventCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, eventCount);

        await using var outboxCountCommand = new NpgsqlCommand("SELECT COUNT(*) FROM outbox;", connection);
        var outboxCount = (long)(await outboxCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, outboxCount);
    }

    private static IngestEventRequest BuildRequest(string idempotencyKey)
    {
        return new IngestEventRequest
        {
            SourceEventId = "evt_src_123",
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"paymentId":"pay_123","amount":1200}""").RootElement.Clone(),
            Metadata = JsonDocument.Parse("""{"source":"integration-tests"}""").RootElement.Clone(),
            IdempotencyKey = idempotencyKey
        };
    }

    private Task<HttpResponseMessage> PostEventAsync(IngestEventRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.TryAddWithoutValidation("Authorization", tenantAAuthHeaderValue);
        return client.SendAsync(message);
    }

    private Task<HttpResponseMessage> GetEventAsync(Guid eventId, string? authHeader = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, $"/events/{eventId}");
        message.Headers.TryAddWithoutValidation(
            "Authorization",
            authHeader ?? tenantAAuthHeaderValue);
        return client.SendAsync(message);
    }
}

public sealed class PostgresApiFixture : IAsyncLifetime
{
    public const string TenantAPublicKey = "key_test_ingest_a";
    public const string TenantAApiSecret = "super-secret-a";
    public const string TenantBPublicKey = "key_test_ingest_b";
    public const string TenantBApiSecret = "super-secret-b";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("integrios")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public WebApplicationFactory<Program> WebFactory { get; private set; } = null!;
    public string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await InitializeSchemaAsync();
        WebFactory = BuildWebFactory();
    }

    public async Task DisposeAsync()
    {
        WebFactory.Dispose();
        await container.DisposeAsync();
    }

    public async Task ResetDataAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        const string resetSql = """
            TRUNCATE TABLE outbox, events, api_keys, tenants RESTART IDENTITY CASCADE;
            """;
        await using (var resetCommand = new NpgsqlCommand(resetSql, connection))
        {
            await resetCommand.ExecuteNonQueryAsync();
        }

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var credentialAId = Guid.NewGuid();
        var credentialBId = Guid.NewGuid();
        var secretHashA = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TenantAApiSecret))).ToLowerInvariant();
        var secretHashB = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TenantBApiSecret))).ToLowerInvariant();

        const string seedSql = """
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantAId, 'test-tenant-a', 'Test Tenant A', 'active', now(), now()),
                (@TenantBId, 'test-tenant-b', 'Test Tenant B', 'active', now(), now());

            INSERT INTO api_keys (
                id,
                tenant_id,
                name,
                key_id,
                secret_hash,
                scopes,
                status,
                created_at
            )
            VALUES (
                @CredentialAId,
                @TenantAId,
                'test-ingest-key-a',
                @PublicKeyA,
                @SecretHashA,
                ARRAY['events.write'],
                'active',
                now()
            ),
            (
                @CredentialBId,
                @TenantBId,
                'test-ingest-key-b',
                @PublicKeyB,
                @SecretHashB,
                ARRAY['events.write'],
                'active',
                now()
            );
            """;

        await using var seedCommand = new NpgsqlCommand(seedSql, connection);
        seedCommand.Parameters.AddWithValue("TenantAId", tenantAId);
        seedCommand.Parameters.AddWithValue("TenantBId", tenantBId);
        seedCommand.Parameters.AddWithValue("CredentialAId", credentialAId);
        seedCommand.Parameters.AddWithValue("CredentialBId", credentialBId);
        seedCommand.Parameters.AddWithValue("PublicKeyA", TenantAPublicKey);
        seedCommand.Parameters.AddWithValue("PublicKeyB", TenantBPublicKey);
        seedCommand.Parameters.AddWithValue("SecretHashA", secretHashA);
        seedCommand.Parameters.AddWithValue("SecretHashB", secretHashB);
        await seedCommand.ExecuteNonQueryAsync();
    }

    private async Task InitializeSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var migrationPath in ResolveMigrationPaths())
        {
            var migrationSql = await File.ReadAllTextAsync(migrationPath);
            await using var migrationCommand = new NpgsqlCommand(migrationSql, connection);
            await migrationCommand.ExecuteNonQueryAsync();
        }
    }

    private WebApplicationFactory<Program> BuildWebFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = ConnectionString
                }));

            // The app builds its data source during startup; replace DB services explicitly
            // so repositories resolve against the container connection string in integration tests.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.RemoveAll<IDbConnectionFactory>();

                services.AddSingleton(_ =>
                {
                    var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);
                    return dataSourceBuilder.Build();
                });
                services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
            });
        });
    }

    private static IReadOnlyList<string> ResolveMigrationPaths()
    {
        var repoRoot = Environment.GetEnvironmentVariable("INTEGRIOS_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var envMigrationDirectory = Path.Combine(repoRoot, "db", "migrations");
            if (Directory.Exists(envMigrationDirectory))
                return Directory.GetFiles(envMigrationDirectory, "*.sql")
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToArray();
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "Integrios.slnx");
            if (File.Exists(solutionPath))
            {
                var migrationDirectory = Path.Combine(directory.FullName, "db", "migrations");
                return Directory.GetFiles(migrationDirectory, "*.sql")
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToArray();
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
