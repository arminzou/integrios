using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Integrios.Application.Authoring.SubscriberKeys;
using Integrios.Application.Subscriber;
using Integrios.Infrastructure.Connectors;
using Integrios.Ingestion;
using Integrios.Subscriber;
using Integrios.Subscriber.Auth;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.FunctionalTests.Admin;

public sealed class SubscriberKeysTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private HttpClient admin = null!;
    private Guid destinationId;
    private string PathFor(Guid tenantId, Guid destination) => $"/admin/tenants/{tenantId}/destinations/{destination}/subscriber-keys";
    private string KeysPath => PathFor(fixture.TenantId, destinationId);

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        admin = fixture.WebFactory.CreateClient();
        destinationId = await CreatePullDestinationAsync("orders-service");
    }

    private async Task<Guid> CreatePullDestinationAsync(string name)
    {
        HttpResponseMessage response = await admin.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/destinations",
            new { connector_id = BuiltInConnectors.Pull.Id, name, configuration = new { } }));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public Task DisposeAsync() { admin.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Lifecycle_ShowsTokenOnceRequiresRevocationAndErasesHash()
    {
        CreateSubscriberKeyResult created = await CreateAsync();
        created.Token.Length.ShouldBe(73);
        created.SubscriberKey.KeyPrefix.ShouldBe(created.Token[..16]);
        created.SubscriberKey.DestinationId.ShouldBe(destinationId);
        string path = $"{KeysPath}/{created.SubscriberKey.Id}";
        string json = await (await admin.SendAsync(AdminRequest(HttpMethod.Get, path))).Content.ReadAsStringAsync();
        json.ShouldNotContain(created.Token);
        json.ShouldNotContain("key_hash");
        await using var connection = fixture.CreateConnection();
        string hash = await connection.QuerySingleAsync<string>("SELECT key_hash FROM subscriber_keys WHERE id = @Id", new { created.SubscriberKey.Id });
        hash.ShouldBe("sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(created.Token))).ToLowerInvariant());
        (await admin.SendAsync(AdminRequest(HttpMethod.Delete, path))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.SendAsync(AdminRequest(HttpMethod.Post, path + "/revoke"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.SendAsync(AdminRequest(HttpMethod.Delete, path))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.SendAsync(AdminRequest(HttpMethod.Get, path))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await connection.QuerySingleAsync<string>("SELECT key_hash FROM subscriber_keys WHERE id = @Id", new { created.SubscriberKey.Id }))
            .ShouldBe($"deleted:{created.SubscriberKey.Id:N}");
        var list = await (await admin.SendAsync(AdminRequest(HttpMethod.Get, KeysPath))).Content.ReadFromJsonAsync<SubscriberKeyListDto>(HostJson.Options);
        list!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Admin_RejectsOtherScopesAndHttpDestinations()
    {
        CreateSubscriberKeyResult created = await CreateAsync();
        foreach (string wrongPath in new[] { PathFor(fixture.OtherTenantId, destinationId), PathFor(fixture.TenantId, fixture.DestinationId) })
        {
            (await admin.SendAsync(AdminRequest(HttpMethod.Get, $"{wrongPath}/{created.SubscriberKey.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await admin.SendAsync(AdminRequest(HttpMethod.Post, $"{wrongPath}/{created.SubscriberKey.Id}/revoke"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await admin.SendAsync(AdminRequest(HttpMethod.Delete, $"{wrongPath}/{created.SubscriberKey.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await admin.SendAsync(AdminRequest(HttpMethod.Post, wrongPath, new { name = "wrong" }))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }
    }

    [Fact]
    public async Task DeliveryHost_AuthenticatesOnlyItsDestinationAndThrottlesUse()
    {
        CreateSubscriberKeyResult created = await CreateAsync();
        using var host = DeliveryHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        var identity = await client.GetFromJsonAsync<IdentityResponse>("/test/subscriber", HostJson.Options);
        identity!.TenantId.ShouldBe(fixture.TenantId.ToString());
        identity.DestinationId.ShouldBe(destinationId.ToString());
        identity.KeyId.ShouldBe(created.SubscriberKey.Id.ToString());
        DateTimeOffset firstUse = (await GetAsync(created.SubscriberKey.Id)).LastUsedAt!.Value;
        (await client.GetAsync("/test/subscriber")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(created.SubscriberKey.Id)).LastUsedAt.ShouldBe(firstUse);
        var recorder = host.Services.GetRequiredService<ISubscriberKeyUseRecorder>();
        await recorder.RecordUseAsync(created.SubscriberKey.Id, firstUse.AddMinutes(59), CancellationToken.None);
        (await GetAsync(created.SubscriberKey.Id)).LastUsedAt.ShouldBe(firstUse);
        await recorder.RecordUseAsync(created.SubscriberKey.Id, firstUse.AddHours(1), CancellationToken.None);
        (await GetAsync(created.SubscriberKey.Id)).LastUsedAt.ShouldBe(firstUse.AddHours(1));
    }

    [Fact]
    public async Task DeliveryHost_AuthenticatesEachKeyAsItsOwnDestination()
    {
        CreateSubscriberKeyResult keyA = await CreateAsync();
        Guid destinationB = await CreatePullDestinationAsync("billing-service");
        var responseB = await admin.SendAsync(AdminRequest(HttpMethod.Post, PathFor(fixture.TenantId, destinationB), new { name = "billing" }));
        CreateSubscriberKeyResult keyB = (await responseB.Content.ReadFromJsonAsync<CreateSubscriberKeyResult>(HostJson.Options))!;
        using var host = DeliveryHost();
        using var client = host.CreateClient();
        foreach ((CreateSubscriberKeyResult key, Guid expected) in new[] { (keyA, destinationId), (keyB, destinationB) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Token);
            var identity = await client.GetFromJsonAsync<IdentityResponse>("/test/subscriber", HostJson.Options);
            identity!.DestinationId.ShouldBe(expected.ToString());
            identity.KeyId.ShouldBe(key.SubscriberKey.Id.ToString());
        }
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("deleted")]
    [InlineData("inactive-tenant")]
    [InlineData("inactive-destination")]
    [InlineData("deleted-destination")]
    [InlineData("http-destination")]
    public async Task DeliveryHost_RejectsUnavailableAuthority(string state)
    {
        CreateSubscriberKeyResult created = await CreateAsync();
        await using var connection = fixture.CreateConnection();
        string sql = state switch
        {
            "revoked" => "UPDATE subscriber_keys SET revoked_at = @Now WHERE id = @KeyId",
            "deleted" => "UPDATE subscriber_keys SET deleted_at = @Now WHERE id = @KeyId",
            "inactive-tenant" => "UPDATE tenants SET status = 'inactive' WHERE id = @TenantId",
            "inactive-destination" => "UPDATE destinations SET status = 'inactive' WHERE id = @DestinationId",
            "deleted-destination" => "UPDATE destinations SET deleted_at = @Now WHERE id = @DestinationId",
            _ => "UPDATE destinations SET connector_id = @HttpConnectorId WHERE id = @DestinationId",
        };
        await connection.ExecuteAsync(sql, new { KeyId = created.SubscriberKey.Id, fixture.TenantId, DestinationId = destinationId, fixture.HttpConnectorId, Now = DateTimeOffset.UtcNow });
        using var host = DeliveryHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        (await client.GetAsync("/test/subscriber")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await connection.QuerySingleAsync<DateTime?>("SELECT last_used_at FROM subscriber_keys WHERE id = @Id", new { created.SubscriberKey.Id })).ShouldBeNull();
    }

    [Fact]
    public async Task CredentialKinds_CannotCrossHostBoundaries()
    {
        CreateSubscriberKeyResult created = await CreateAsync();
        using var host = DeliveryHost();
        using var client = host.CreateClient();
        var tenantKeyResponse = await admin.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/tenant-api-keys", new { name = "ingestion" }));
        using var tenantKey = JsonDocument.Parse(await tenantKeyResponse.Content.ReadAsStringAsync());
        foreach (string token in new[] { tenantKey.RootElement.GetProperty("token").GetString()!, "intg_sub_" + new string('0', 64) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await client.GetAsync("/test/subscriber")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        using var ingestionHost = new TestHostBuilder(IngestionHost.CreateBuilder, IngestionHost.ConfigurePipeline).WithSettings(fixture.Configuration);
        using var ingestion = ingestionHost.CreateClient();
        ingestion.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        (await ingestion.PostAsJsonAsync("/events?source_id=" + Guid.NewGuid(), new { event_type = "test", payload = new { } })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private TestHostBuilder DeliveryHost() => new TestHostBuilder(SubscriberHost.CreateBuilder, (app, port) =>
    {
        SubscriberHost.ConfigurePipeline(app, port);
        app.MapGet("/test/subscriber", (HttpContext context) => Results.Ok(new IdentityResponse(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            context.User.FindFirstValue(SubscriberKeyAuthHandler.TenantIdClaim)!,
            context.User.FindFirstValue(SubscriberKeyAuthHandler.DestinationIdClaim)!))).RequireAuthorization();
    }).WithSettings(fixture.Configuration);

    private async Task<CreateSubscriberKeyResult> CreateAsync()
    {
        var response = await admin.SendAsync(AdminRequest(HttpMethod.Post, KeysPath, new { name = "consumer", description = "test" }));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreateSubscriberKeyResult>(HostJson.Options))!;
    }

    private async Task<SubscriberKeyDto> GetAsync(Guid id) =>
        (await (await admin.SendAsync(AdminRequest(HttpMethod.Get, $"{KeysPath}/{id}"))).Content.ReadFromJsonAsync<SubscriberKeyDto>(HostJson.Options))!;

    private sealed record IdentityResponse(string KeyId, string TenantId, string DestinationId);
}
