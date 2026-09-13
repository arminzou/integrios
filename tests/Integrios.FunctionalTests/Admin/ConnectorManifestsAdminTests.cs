using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Events;
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
        createdResponse.Headers.GetValues("X-Integrios-Connector-Manifest-Outcome").ShouldHaveSingleItem()
            .ShouldBe(nameof(ConnectorManifestApplyOutcome.Created));
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
        unchangedResponse.Headers.GetValues("X-Integrios-Connector-Manifest-Outcome").ShouldHaveSingleItem()
            .ShouldBe(nameof(ConnectorManifestApplyOutcome.Unchanged));
        ConnectorDto unchanged = (await unchangedResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        unchanged.Id.ShouldBe(created.Id);
        unchanged.UpdatedAt.ShouldBe(created.UpdatedAt);

        JsonElement renamedManifest = Manifest(contractVersion: 1, name: "Improved API");
        HttpResponseMessage renamedResponse = await ApplyAsync(1, renamedManifest);
        renamedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        renamedResponse.Headers.GetValues("X-Integrios-Connector-Manifest-Outcome").ShouldHaveSingleItem()
            .ShouldBe(nameof(ConnectorManifestApplyOutcome.PresentationReconciled));
        ConnectorDto renamed = (await renamedResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        renamed.Id.ShouldBe(created.Id);
        renamed.Name.ShouldBe("Improved API");

        await ExecuteAsync("UPDATE connectors SET status = 'disabled' WHERE id = @Id", created.Id);
        JsonElement disabledRenamedManifest = Manifest(contractVersion: 1, name: "Disabled API");
        HttpResponseMessage disabledRenameResponse = await ApplyAsync(1, disabledRenamedManifest);
        ConnectorDto disabledRename = (await disabledRenameResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        disabledRename.Name.ShouldBe("Disabled API");
        disabledRename.Status.ShouldBe("disabled");

        JsonElement functionalChange = Json(disabledRenamedManifest.GetRawText().Replace(
            "\"additionalProperties\":false",
            "\"additionalProperties\":true",
            StringComparison.Ordinal));
        HttpResponseMessage conflictResponse = await ApplyAsync(1, functionalChange);
        conflictResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        ConnectorDto retained = (await GetVersionAsync(1))!;
        retained.Manifest.GetProperty("presentation").GetProperty("name").GetString().ShouldBe(
            "Disabled API");
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
            item.Name == "Disabled API");
        list.Items.ShouldContain(item =>
            item.Id == createdV2.Id &&
            item.ContractVersion == 2 &&
            item.Name == "Example API v2");
    }

    [Fact]
    public async Task Apply_RejectsRouteIdentityMismatchAndRetiredSourceContracts()
    {
        HttpResponseMessage identityMismatch = await ApplyAsync(2, Manifest(contractVersion: 1, name: "Example API"));
        identityMismatch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        JsonElement sourceCapable = Json(Manifest(1, "Example API").GetRawText().Replace(
            "\"direction\":\"destination\"",
            "\"direction\":\"both\","
            + "\"source_configuration_schema\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true}",
            StringComparison.Ordinal));
        HttpResponseMessage sourceCapableResponse = await ApplyAsync(1, sourceCapable);
        sourceCapableResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        JsonElement sourceContract = Json(sourceCapable.GetRawText().Replace(
            "\"presentation\":",
            "\"source_contracts\":[{\"key\":\"event_json\",\"contract_version\":1,\"config\":{}}],\"presentation\":",
            StringComparison.Ordinal));
        HttpResponseMessage sourceContractResponse = await ApplyAsync(1, sourceContract);
        sourceContractResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
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

    [Theory]
    [InlineData("source", true, false)]
    [InlineData("destination", false, true)]
    [InlineData("both", true, true)]
    public async Task Compose_ReturnsAParseableManifestWithEveryPlatformScheme(
        string direction,
        bool sourceCapable,
        bool destinationCapable)
    {
        string key = $"guided_{direction}";
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            $"/admin/connectors/{key}/versions/1/compose",
            Json($$$"""{"name":"Guided","description":null,"direction":"{{{direction}}}"}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>(HostJson.Options);
        JsonElement document = result.GetProperty("manifest");
        ConnectorManifest manifest = ConnectorManifestParser.Parse(
            document,
            new SourceVerifierRegistry([new HmacSha256SourceVerifier()]),
            new DestinationAuthenticatorRegistry([new ApiKeyHeaderAuthenticator(), new BearerTokenAuthenticator()]));

        manifest.SourceConfigurationSchema.HasValue.ShouldBe(sourceCapable);
        manifest.DestinationConfigurationSchema.HasValue.ShouldBe(destinationCapable);
        manifest.SourceVerification.AllowUnverified.ShouldBeTrue();
        manifest.DestinationAuthentication.AllowUnauthenticated.ShouldBeTrue();
        manifest.SourceVerification.Schemes.Select(scheme => scheme.Scheme)
            .ShouldBe(sourceCapable ? ["hmac_sha256"] : []);
        manifest.DestinationAuthentication.Schemes.Select(scheme => scheme.Scheme)
            .ShouldBe(destinationCapable ? ["api_key_header", "bearer_token"] : []);
    }

    [Fact]
    public async Task Compose_CarriesForwardTheLatestVersionsTightenedContract()
    {
        (await SendAsync(HttpMethod.Put, "/admin/connectors/carry_forward/versions/1", CarryForwardManifest(1, "old.event")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SendAsync(HttpMethod.Put, "/admin/connectors/carry_forward/versions/2", CarryForwardManifest(2, "latest.event")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            "/admin/connectors/carry_forward/versions/3/compose",
            Json("""{"name":"Next","description":"New description","direction":"both"}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>(HostJson.Options);
        ConnectorManifest manifest = ConnectorManifestParser.Parse(
            result.GetProperty("manifest"),
            new SourceVerifierRegistry([new HmacSha256SourceVerifier()]),
            new DestinationAuthenticatorRegistry([new ApiKeyHeaderAuthenticator(), new BearerTokenAuthenticator()]));
        manifest.ContractVersion.ShouldBe(3);
        manifest.Presentation.Name.ShouldBe("Next");
        manifest.Presentation.EventTypes.ShouldBe(["latest.event"]);
        manifest.Presentation.AuthoringPresets.ShouldHaveSingleItem()
            .GetProperty("label").GetString().ShouldBe("Latest");
        manifest.SourceConfigurationSchema!.Value.GetProperty("required")[0].GetString().ShouldBe("region");
        manifest.DestinationConfigurationSchema!.Value.GetProperty("required").GetArrayLength().ShouldBe(2);
        manifest.DestinationAuthentication.Schemes.ShouldHaveSingleItem().Scheme.ShouldBe("bearer_token");
        manifest.SourceVerification.AllowUnverified.ShouldBeTrue();
        manifest.DestinationAuthentication.AllowUnauthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task GuidedConnector_AllowsSourcesAndDestinationsToSelectEveryComposedScheme()
    {
        using HttpResponseMessage composedResponse = await SendAsync(
            HttpMethod.Post,
            "/admin/connectors/guided_both/versions/1/compose",
            Json("""{"name":"Guided both","description":null,"direction":"both"}"""));
        composedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement composed = await composedResponse.Content.ReadFromJsonAsync<JsonElement>(HostJson.Options);

        using HttpResponseMessage appliedResponse = await SendAsync(
            HttpMethod.Put,
            "/admin/connectors/guided_both/versions/1",
            composed.GetProperty("manifest"));
        appliedResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        ConnectorDto connector = (await appliedResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;

        using HttpResponseMessage topicResponse = await SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            Json("""{"key":"guided-topic"}"""));
        topicResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid topicId = (await topicResponse.Content.ReadFromJsonAsync<JsonElement>(HostJson.Options)).GetProperty("id").GetGuid();

        using HttpResponseMessage sourceResponse = await SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/sources",
            JsonSerializer.SerializeToElement(new
            {
                connector_id = connector.Id,
                topic_id = topicId,
                name = "guided-source",
                type = "webhook",
                configuration = new { },
                verification = new
                {
                    scheme = "hmac_sha256",
                    config = new { },
                    secret_refs = new { secret = "guided_signing_secret" },
                },
            }));
        sourceResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        foreach ((string name, object authentication) in new (string, object)[]
        {
            ("guided-api-key", new
            {
                scheme = "api_key_header",
                config = new { header_name = "X-Api-Key" },
                secret_refs = new { api_key = "guided_api_key" },
            }),
            ("guided-bearer", new
            {
                scheme = "bearer_token",
                config = new { },
                secret_refs = new { token = "guided_bearer_token" },
            }),
        })
        {
            using HttpResponseMessage destinationResponse = await SendAsync(
                HttpMethod.Post,
                $"/admin/tenants/{fixture.TenantId}/destinations",
                JsonSerializer.SerializeToElement(new
                {
                    connector_id = connector.Id,
                    name,
                    configuration = new { base_uri = "https://example.invalid/events" },
                    authentication,
                }));
            destinationResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
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

    private static JsonElement CarryForwardManifest(int contractVersion, string eventType)
    {
        string preset = contractVersion == 2 ? "Latest" : "Old";
        return Json($$$"""
            {
              "manifest_schema_version":1,
              "key":"carry_forward",
              "contract_version":{{{contractVersion}}},
              "direction":"both",
              "source_configuration_schema":{"type":"object","properties":{"region":{"type":"string"}},"required":["region"],"additionalProperties":false},
              "destination_configuration_schema":{"type":"object","properties":{"base_uri":{"type":"string","format":"uri"},"operation":{"type":"string"}},"required":["base_uri","operation"],"additionalProperties":false},
              "source_verification":{"allow_unverified":false,"schemes":[{"scheme":"hmac_sha256","required_config":[],"required_secret_refs":["secret"]}]},
              "destination_authentication":{"allow_unauthenticated":false,"schemes":[{"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}]},
              "presentation":{"name":"Previous","event_types":["{{{eventType}}}"],"authoring_presets":[{"label":"{{{preset}}}"}]}
            }
            """);
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
