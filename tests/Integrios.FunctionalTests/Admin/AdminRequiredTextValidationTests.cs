using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class AdminRequiredTextValidationTests
    : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public AdminRequiredTextValidationTests(AdminApiFixture fixture)
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

    [Theory]
    [InlineData("tenant-create")]
    [InlineData("tenant-update")]
    [InlineData("topic-create")]
    [InlineData("tenant-api-key-create")]
    [InlineData("connection-create")]
    [InlineData("connection-update")]
    [InlineData("subscription-create")]
    [InlineData("subscription-update")]
    public async Task RequiredName_Null_ReturnsFieldValidation(string operation)
    {
        using HttpResponseMessage response = await client.SendAsync(Request(operation, null));

        await AssertNameValidationAsync(response);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTenant_NameWithoutText_ReturnsFieldValidation(string name)
    {
        using HttpResponseMessage response = await client.SendAsync(Request("tenant-create", name));

        await AssertNameValidationAsync(response);
    }

    [Fact]
    public async Task CreateTenant_OmittedName_ReturnsFieldValidation()
    {
        using HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/tenants",
            new { slug = "omitted-name" }));

        await AssertNameValidationAsync(response);
    }

    private HttpRequestMessage Request(string operation, string? name) => operation switch
    {
        "tenant-create" => AdminRequest(HttpMethod.Post, "/admin/tenants", new { slug = "required-name", name }),
        "tenant-update" => AdminRequest(HttpMethod.Patch, $"/admin/tenants/{fixture.TenantId}", new { name }),
        "topic-create" => AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/topics", new { name }),
        "tenant-api-key-create" => AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys",
            new { name }),
        "connection-create" => AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new { connector_id = fixture.HttpConnectorId, name, config = new { } }),
        "connection-update" => AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{fixture.SourceConnectionId}",
            new { name, config = new { } }),
        "subscription-create" => SubscriptionRequest(HttpMethod.Post, Guid.NewGuid(), Guid.Empty, name),
        "subscription-update" => SubscriptionRequest(HttpMethod.Patch, Guid.NewGuid(), Guid.NewGuid(), name),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private HttpRequestMessage SubscriptionRequest(HttpMethod method, Guid topicId, Guid subscriptionId, string? name)
    {
        string path = $"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions";
        if (subscriptionId != Guid.Empty)
            path += $"/{subscriptionId}";

        return AdminRequest(method, path, new
        {
            name,
            match_rules = new { event_type = "validation.test" },
            destination_connection_id = fixture.SourceConnectionId,
            order_index = 0
        });
    }

    private static async Task AssertNameValidationAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.MediaType.ShouldBe("application/problem+json");

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(422);
        string.IsNullOrWhiteSpace(body.RootElement.GetProperty("trace_id").GetString()).ShouldBeFalse();
        body.RootElement.GetProperty("errors").GetProperty("name")[0].GetString().ShouldBe("Name is required.");
    }
}
