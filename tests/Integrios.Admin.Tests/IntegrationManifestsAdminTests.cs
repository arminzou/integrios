using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Integrations;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Integrios.Admin.Tests;

public sealed class IntegrationManifestsAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;
    private readonly JsonSerializerOptions webJson = new(JsonSerializerDefaults.Web);
    private HttpClient client = null!;

    public IntegrationManifestsAdminTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Apply_EnforcesImmutableVersionsAndReconcilesOnlyPresentation()
    {
        JsonElement version1 = Manifest(contractVersion: 1, name: "Example API");
        HttpResponseMessage createdResponse = await ApplyAsync(1, version1);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.Equal(
            "/admin/integrations/example_api/versions/1",
            createdResponse.Headers.Location?.OriginalString);
        IntegrationResponse created = (await createdResponse.Content.ReadFromJsonAsync<IntegrationResponse>(webJson))!;
        Assert.Equal(1, created.ContractVersion);
        Assert.Equal("example_api", created.Manifest.GetProperty("key").GetString());
        JsonElement storedSchemes = created.Manifest.GetProperty("destination_authentication").GetProperty("schemes");
        Assert.Equal(2, storedSchemes.GetArrayLength());
        Assert.Equal("api_key_header", storedSchemes[0].GetProperty("scheme").GetString());
        Assert.Equal("bearer_token", storedSchemes[1].GetProperty("scheme").GetString());
        Assert.Equal(
            "Example API",
            created.Manifest.GetProperty("presentation").GetProperty("name").GetString());

        HttpResponseMessage unchangedResponse = await ApplyAsync(1, Reordered(version1));
        Assert.Equal(HttpStatusCode.OK, unchangedResponse.StatusCode);
        IntegrationResponse unchanged = (await unchangedResponse.Content.ReadFromJsonAsync<IntegrationResponse>(webJson))!;
        Assert.Equal(created.Id, unchanged.Id);
        Assert.Equal(created.UpdatedAt, unchanged.UpdatedAt);

        JsonElement renamedManifest = Manifest(contractVersion: 1, name: "Improved API");
        HttpResponseMessage renamedResponse = await ApplyAsync(1, renamedManifest);
        Assert.Equal(HttpStatusCode.OK, renamedResponse.StatusCode);
        IntegrationResponse renamed = (await renamedResponse.Content.ReadFromJsonAsync<IntegrationResponse>(webJson))!;
        Assert.Equal(created.Id, renamed.Id);
        Assert.Equal("Improved API", renamed.Name);

        await ExecuteAsync("UPDATE integrations SET status = 'disabled' WHERE id = @Id", created.Id);
        HttpResponseMessage disabledNoOpResponse = await ApplyAsync(1, renamedManifest);
        IntegrationResponse disabledNoOp = (await disabledNoOpResponse.Content.ReadFromJsonAsync<IntegrationResponse>(webJson))!;
        Assert.Equal("disabled", disabledNoOp.Status);

        JsonElement functionalChange = Json(renamedManifest.GetRawText().Replace(
            "\"additionalProperties\":false",
            "\"additionalProperties\":true",
            StringComparison.Ordinal));
        HttpResponseMessage conflictResponse = await ApplyAsync(1, functionalChange);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

        IntegrationResponse retained = (await GetVersionAsync(1))!;
        Assert.Equal(
            "Improved API",
            retained.Manifest.GetProperty("presentation").GetProperty("name").GetString());
        Assert.False(retained.Manifest
            .GetProperty("destination_configuration_schema")
            .GetProperty("additionalProperties")
            .GetBoolean());

        JsonElement version2 = Manifest(contractVersion: 2, name: "Example API v2");
        HttpResponseMessage version2Response = await ApplyAsync(2, version2);
        Assert.Equal(HttpStatusCode.Created, version2Response.StatusCode);
        IntegrationResponse createdV2 = (await version2Response.Content.ReadFromJsonAsync<IntegrationResponse>(webJson))!;
        Assert.NotEqual(created.Id, createdV2.Id);

        Assert.Equal(2L, await CountAsync(
            "integrations",
            "key = 'example_api' AND contract_version IN (1, 2)"));

        using HttpResponseMessage listResponse = await SendAsync(HttpMethod.Get, "/admin/integrations");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        IntegrationListResponse list = (await listResponse.Content.ReadFromJsonAsync<IntegrationListResponse>(webJson))!;
        Assert.Contains(list.Items, item =>
            item.Id == created.Id &&
            item.ContractVersion == 1 &&
            item.Manifest.GetProperty("presentation").GetProperty("name").GetString() == "Improved API");
        Assert.Contains(list.Items, item =>
            item.Id == createdV2.Id &&
            item.ContractVersion == 2 &&
            item.Manifest.GetProperty("presentation").GetProperty("name").GetString() == "Example API v2");
    }

    [Fact]
    public async Task Apply_RejectsRouteIdentityMismatchAndBuiltInAdapterSelection()
    {
        HttpResponseMessage identityMismatch = await ApplyAsync(2, Manifest(contractVersion: 1, name: "Example API"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, identityMismatch.StatusCode);

        JsonElement builtInAdapter = Json(Manifest(1, "Example API").GetRawText().Replace(
            "\"direction\":\"destination\"",
            "\"direction\":\"both\",\"source_configuration_schema\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true},\"built_in_source_adapter\":\"github\"",
            StringComparison.Ordinal));
        HttpResponseMessage adapterResponse = await ApplyAsync(1, builtInAdapter);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, adapterResponse.StatusCode);
    }

    [Fact]
    public async Task Apply_ConcurrentIdenticalCreatesReturnOneCreatedAndOneNoOp()
    {
        JsonElement manifest = Manifest(1, "Example API");
        HttpResponseMessage[] responses = await Task.WhenAll(ApplyAsync(1, manifest), ApplyAsync(1, manifest));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1L, await CountAsync("integrations", "key = 'example_api' AND contract_version = 1"));
    }

    private async Task<IntegrationResponse?> GetVersionAsync(int contractVersion)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            $"/admin/integrations/example_api/versions/{contractVersion}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<IntegrationResponse>(webJson);
    }

    private Task<HttpResponseMessage> ApplyAsync(int contractVersion, JsonElement manifest) =>
        SendAsync(HttpMethod.Put, $"/admin/integrations/example_api/versions/{contractVersion}", manifest);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonElement? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is JsonElement content)
            request.Content = JsonContent.Create(content);
        return client.SendAsync(request);
    }

    private async Task<long> CountAsync(string table, string where)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static JsonElement Manifest(int contractVersion, string name) => Json($$$"""
        {
          "manifest_schema_version":1,
          "key":"example_api",
          "contract_version":{{{contractVersion}}},
          "direction":"destination",
          "destination_configuration_schema":{
            "type":"object",
            "properties":{"base_uri":{"type":"string","format":"uri"}},
            "required":["base_uri"],
            "additionalProperties":false
          },
          "source_verification":{"allow_unverified":true,"schemes":[]},
          "destination_authentication":{
            "allow_unauthenticated":false,
            "schemes":[
              {"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]},
              {"scheme":"api_key_header","required_config":["header_name"],"required_secret_refs":["api_key"]}
            ]
          },
          "presentation":{"name":"{{{name}}}","event_types":[],"authoring_presets":[]}
        }
        """);

    private static JsonElement Reordered(JsonElement manifest) => Json($$$"""
        {
          "presentation":{{{manifest.GetProperty("presentation").GetRawText()}}},
          "destination_authentication":{
            "allow_unauthenticated":false,
            "schemes":[
              {"scheme":"api_key_header","required_config":["header_name"],"required_secret_refs":["api_key"]},
              {"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}
            ]
          },
          "source_verification":{"allow_unverified":true,"schemes":[]},
          "destination_configuration_schema":{{{manifest.GetProperty("destination_configuration_schema").GetRawText()}}},
          "direction":"destination",
          "contract_version":1,
          "key":"example_api",
          "manifest_schema_version":1
        }
        """);

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
