using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Integrios.Application.Authoring.Connectors;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public sealed class ConnectorManifestsAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public ConnectorManifestsAdminTests(AdminApiFixture fixture)
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
        createdResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        createdResponse.Headers.Location?.OriginalString.ShouldBe(
            "/admin/connectors/example_api/versions/1");
        ConnectorDto created = (await createdResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        created.ContractVersion.ShouldBe(1);
        created.Manifest.GetProperty("key").GetString().ShouldBe("example_api");
        JsonElement storedSchemes = created.Manifest.GetProperty("destination_authentication").GetProperty("schemes");
        storedSchemes.GetArrayLength().ShouldBe(2);
        storedSchemes[0].GetProperty("scheme").GetString().ShouldBe("api_key_header");
        storedSchemes[1].GetProperty("scheme").GetString().ShouldBe("bearer_token");
        created.Manifest.GetProperty("presentation").GetProperty("name").GetString().ShouldBe(
            "Example API");

        HttpResponseMessage unchangedResponse = await ApplyAsync(1, Reordered(version1));
        unchangedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        ConnectorDto unchanged = (await unchangedResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        unchanged.Id.ShouldBe(created.Id);
        unchanged.UpdatedAt.ShouldBe(created.UpdatedAt);

        JsonElement renamedManifest = Manifest(contractVersion: 1, name: "Improved API");
        HttpResponseMessage renamedResponse = await ApplyAsync(1, renamedManifest);
        renamedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        ConnectorDto renamed = (await renamedResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        renamed.Id.ShouldBe(created.Id);
        renamed.Name.ShouldBe("Improved API");

        await ExecuteAsync("UPDATE connectors SET status = 'disabled' WHERE id = @Id", created.Id);
        HttpResponseMessage disabledNoOpResponse = await ApplyAsync(1, renamedManifest);
        ConnectorDto disabledNoOp = (await disabledNoOpResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        disabledNoOp.Status.ShouldBe("disabled");

        JsonElement functionalChange = Json(renamedManifest.GetRawText().Replace(
            "\"additionalProperties\":false",
            "\"additionalProperties\":true",
            StringComparison.Ordinal));
        HttpResponseMessage conflictResponse = await ApplyAsync(1, functionalChange);
        conflictResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        ConnectorDto retained = (await GetVersionAsync(1))!;
        retained.Manifest.GetProperty("presentation").GetProperty("name").GetString().ShouldBe(
            "Improved API");
        retained.Manifest
            .GetProperty("destination_configuration_schema")
            .GetProperty("additionalProperties")
            .GetBoolean().ShouldBeFalse();

        JsonElement version2 = Manifest(contractVersion: 2, name: "Example API v2");
        HttpResponseMessage version2Response = await ApplyAsync(2, version2);
        version2Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        ConnectorDto createdV2 = (await version2Response.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        createdV2.Id.ShouldNotBe(created.Id);

        (await CountAsync(
            "connectors",
            $"{fixture.KeyColumn} = 'example_api' AND contract_version IN (1, 2)")).ShouldBe(2L);

        using HttpResponseMessage listResponse = await SendAsync(HttpMethod.Get, "/admin/connectors");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        ConnectorListDto list = (await listResponse.Content.ReadFromJsonAsync<ConnectorListDto>(HostJson.Options))!;
        list.Items.ShouldContain(item =>
            item.Id == created.Id &&
            item.ContractVersion == 1 &&
            item.Manifest.GetProperty("presentation").GetProperty("name").GetString() == "Improved API");
        list.Items.ShouldContain(item =>
            item.Id == createdV2.Id &&
            item.ContractVersion == 2 &&
            item.Manifest.GetProperty("presentation").GetProperty("name").GetString() == "Example API v2");
    }

    [Fact]
    public async Task Apply_RejectsRouteIdentityMismatchAndAcceptsDeclarativeSourceContract()
    {
        HttpResponseMessage identityMismatch = await ApplyAsync(2, Manifest(contractVersion: 1, name: "Example API"));
        identityMismatch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        JsonElement sourceContract = Json(Manifest(1, "Example API").GetRawText().Replace(
            "\"direction\":\"destination\"",
            "\"direction\":\"both\","
            + "\"source_configuration_schema\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true},"
            + "\"source_contracts\":[{\"key\":\"event_json\",\"contract_version\":1,\"config\":{}}]",
            StringComparison.Ordinal));
        HttpResponseMessage sourceContractResponse = await ApplyAsync(1, sourceContract);
        sourceContractResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Apply_ConcurrentIdenticalCreatesReturnOneCreatedAndOneNoOp()
    {
        JsonElement manifest = Manifest(1, "Example API");
        HttpResponseMessage[] responses = await Task.WhenAll(ApplyAsync(1, manifest), ApplyAsync(1, manifest));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        (await CountAsync("connectors", $"{fixture.KeyColumn} = 'example_api' AND contract_version = 1")).ShouldBe(1L);
    }

    private async Task<ConnectorDto?> GetVersionAsync(int contractVersion)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            $"/admin/connectors/example_api/versions/{contractVersion}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options);
    }

    private Task<HttpResponseMessage> ApplyAsync(int contractVersion, JsonElement manifest) =>
        SendAsync(HttpMethod.Put, $"/admin/connectors/example_api/versions/{contractVersion}", manifest);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonElement? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalOperatorAuthHeader);
        if (body is JsonElement content)
            request.Content = JsonContent.Create(content);
        return client.SendAsync(request);
    }

    private async Task<long> CountAsync(string table, string where)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {where}");
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, new { Id = id });
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
