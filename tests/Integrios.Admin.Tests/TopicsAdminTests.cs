using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Topics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.Admin.Tests;

public sealed class TopicsAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    // Matches ASP.NET Core's camelCase defaults for positional record deserialization.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public TopicsAdminTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
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
    public async Task CreateTopic_ReturnsCreated_WithCorrectBody()
    {
        var response = await PostTopicAsync(new
        {
            name = "payments",
            description = "Payment events stream"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(fixture.TenantId, body.TenantId);
        Assert.Equal("payments", body.Name);
        Assert.Equal("Payment events stream", body.Description);
        Assert.Equal("active", body.Status);
        Assert.NotEqual(default, body.Id);
        Assert.Empty(body.SourceConnectionIds);
    }

    [Fact]
    public async Task CreateTopic_WithSourceConnections_RoundTrips()
    {
        var response = await PostTopicAsync(new
        {
            name = "orders",
            sourceConnectionIds = new[] { fixture.SourceConnectionId }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Single(body.SourceConnectionIds);
        Assert.Equal(fixture.SourceConnectionId, body.SourceConnectionIds[0]);
    }

    [Fact]
    public async Task GetTopicById_ReturnsTopicWithSources()
    {
        var created = await (await PostTopicAsync(new
        {
            name = "inventory",
            sourceConnectionIds = new[] { fixture.SourceConnectionId }
        })).Content.ReadFromJsonAsync<TopicResponse>(WebJson);

        Assert.NotNull(created);

        var get = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
        Assert.Single(body.SourceConnectionIds);
        Assert.Equal(fixture.SourceConnectionId, body.SourceConnectionIds[0]);
    }

    [Fact]
    public async Task GetTopicById_UnknownId_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListTopics_ReturnsCursorPaginatedResults()
    {
        await PostTopicAsync(new { name = "topic-a" });
        await PostTopicAsync(new { name = "topic-b" });
        await PostTopicAsync(new { name = "topic-c" });

        var page1 = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics?limit=2"));
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);

        var body1 = await page1.Content.ReadFromJsonAsync<TopicListResponse>(WebJson);
        Assert.NotNull(body1);
        Assert.Equal(2, body1.Items.Count);
        Assert.NotNull(body1.NextCursor);

        var page2 = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);

        var body2 = await page2.Content.ReadFromJsonAsync<TopicListResponse>(WebJson);
        Assert.NotNull(body2);
        Assert.Single(body2.Items);
        Assert.Null(body2.NextCursor);

        var allNames = body1.Items.Select(t => t.Name).Concat(body2.Items.Select(t => t.Name)).ToList();
        Assert.Contains("topic-a", allNames);
        Assert.Contains("topic-b", allNames);
        Assert.Contains("topic-c", allNames);
    }

    [Fact]
    public async Task UpdateTopic_UpdatesNameDescriptionAndSources()
    {
        var created = await (await PostTopicAsync(new { name = "old-name" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var patch = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}",
            new
            {
                name = "new-name",
                description = "updated",
                sourceConnectionIds = new[] { fixture.SourceConnectionId }
            }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var body = await patch.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("new-name", body.Name);
        Assert.Equal("updated", body.Description);
        Assert.Single(body.SourceConnectionIds);
        Assert.Equal(fixture.SourceConnectionId, body.SourceConnectionIds[0]);
    }

    [Fact]
    public async Task DeactivateTopic_Returns200_AndStatusBecomesInactive()
    {
        var created = await (await PostTopicAsync(new { name = "to-deactivate" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var deactivate = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var get = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("disabled", body.Status);
    }

    [Fact]
    public async Task DeactivateTopic_UnknownId_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{Guid.NewGuid()}/deactivate"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTopic_WrongTenantKey_Returns403()
    {
        var response = await client.SendAsync(TenantRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name = "forbidden" },
            fixture.OtherTenantAdminKey));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTopic_GlobalKey_CanAccessAnyTenant()
    {
        var created = await (await PostTopicAsync(new { name = "global-visible" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        // confirm it's accessible with the global key (which PostTopicAsync uses)
        var get = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    private Task<HttpResponseMessage> PostTopicAsync(object body) =>
        client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/topics", body));

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }

    private HttpRequestMessage TenantRequest(HttpMethod method, string url, object? body, string authHeader)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", authHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }
}

public sealed class AdminApiFixture : IAsyncLifetime
{
    public const string GlobalAdminPublicKey = "global_admin_key";
    public const string GlobalAdminSecret = "admin_bootstrap_secret";
    public const string GlobalAdminAuthHeader = $"AdminKey {GlobalAdminPublicKey}:{GlobalAdminSecret}";

    private static readonly Guid WebhookIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("integrios")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public WebApplicationFactory<Program> WebFactory { get; private set; } = null!;
    public string ConnectionString => container.GetConnectionString();
    public Guid TenantId { get; private set; }
    public Guid SourceConnectionId { get; private set; }
    public string OtherTenantAdminKey { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await RunMigrationsAsync();
        WebFactory = BuildWebFactory();
    }

    public async Task DisposeAsync()
    {
        WebFactory.Dispose();
        await container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // admin_keys must be listed explicitly so CASCADE doesn't surprise us,
        // and we re-seed the global bootstrap key in SeedAsync.
        await using var truncateCmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE subscription_deliveries, delivery_attempts, outbox, events,
                subscriptions, topic_sources, topics, connections, api_keys, admin_keys, tenants
            RESTART IDENTITY CASCADE;
            """, connection);
        await truncateCmd.ExecuteNonQueryAsync();

        await SeedAsync(connection);
    }

    private async Task SeedAsync(NpgsqlConnection connection)
    {
        TenantId = Guid.NewGuid();
        SourceConnectionId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var otherAdminKeyId = Guid.NewGuid();

        var otherKeySecret = "other-tenant-secret";
        var otherKeyHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(otherKeySecret))).ToLowerInvariant();
        OtherTenantAdminKey = $"AdminKey other_tenant_key:{otherKeySecret}";

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantId, 'test-tenant', 'Test Tenant', 'active', now(), now()),
                (@OtherTenantId, 'other-tenant', 'Other Tenant', 'active', now(), now());

            INSERT INTO integrations (id, key, name, direction, status)
            VALUES (@IntegrationId, 'webhook', 'Webhook', 'both', 'active')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES (@SourceConnectionId, @TenantId, @IntegrationId, 'source', '{}', 'active');

            -- Re-seed global bootstrap key (truncated in ResetAsync)
            INSERT INTO admin_keys (tenant_id, public_key, secret_hash, name, created_at)
            VALUES (NULL, 'global_admin_key',
                    'sha256:5af35a0149f5a07231b181c3b4d5d3a76a4c765258533a123b34dfb843599328',
                    'Bootstrap Global Admin Key', now());

            INSERT INTO admin_keys (id, tenant_id, public_key, secret_hash, name, created_at)
            VALUES (@OtherAdminKeyId, @OtherTenantId, 'other_tenant_key', @OtherKeyHash, 'Other Tenant Key', now());
            """, connection);

        cmd.Parameters.AddWithValue("TenantId", TenantId);
        cmd.Parameters.AddWithValue("OtherTenantId", otherTenantId);
        cmd.Parameters.AddWithValue("IntegrationId", WebhookIntegrationId);
        cmd.Parameters.AddWithValue("SourceConnectionId", SourceConnectionId);
        cmd.Parameters.AddWithValue("OtherAdminKeyId", otherAdminKeyId);
        cmd.Parameters.AddWithValue("OtherKeyHash", otherKeyHash);
        await cmd.ExecuteNonQueryAsync();
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

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.RemoveAll<Integrios.Infrastructure.Data.IDbConnectionFactory>();

                services.AddSingleton(_ => new NpgsqlDataSourceBuilder(ConnectionString).Build());
                services.AddSingleton<Integrios.Infrastructure.Data.IDbConnectionFactory,
                    Integrios.Infrastructure.Data.NpgsqlConnectionFactory>();
            });
        });
    }

    private async Task RunMigrationsAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var path in ResolveMigrationPaths())
        {
            var sql = await File.ReadAllTextAsync(path);
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> ResolveMigrationPaths()
    {
        var repoRoot = Environment.GetEnvironmentVariable("INTEGRIOS_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var envDir = Path.Combine(repoRoot, "db", "migrations");
            if (Directory.Exists(envDir))
                return MigrationFiles(envDir);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
                return MigrationFiles(Path.Combine(directory.FullName, "db", "migrations"));
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static IEnumerable<string> MigrationFiles(string dir) =>
        Directory.GetFiles(dir, "*.sql")
            .Where(p => !Path.GetFileName(p).StartsWith("V4__"))
            .OrderBy(GetMigrationVersion)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal);

    private static int GetMigrationVersion(string path)
    {
        var fileName = Path.GetFileName(path);
        var separator = fileName.IndexOf("__", StringComparison.Ordinal);
        if (separator <= 1)
            return int.MaxValue;

        return int.TryParse(fileName[1..separator], out var version)
            ? version
            : int.MaxValue;
    }
}
