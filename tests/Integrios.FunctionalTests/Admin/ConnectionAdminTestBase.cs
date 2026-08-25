using Dapper;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http;

namespace Integrios.FunctionalTests.Admin;

public abstract class ConnectionAdminTestBase : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    protected readonly AdminApiFixture Fixture;
    protected static readonly Guid HttpConnectorId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    protected HttpClient client = default!;

    protected ConnectionAdminTestBase(AdminApiFixture fixture) => Fixture = fixture;

    public async Task InitializeAsync()
    {
        await Fixture.ResetAsync();
        client = Fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected Task<HttpResponseMessage> PostConnectionAsync(string name, string url) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/connections",
            new
            {
                connector_id = HttpConnectorId,
                name,
                config = new { base_uri = url }
            }));

    protected Task<HttpResponseMessage> PostConnectionWithAuthAsync(
        Guid connectorId,
        string name,
        string scheme,
        object authConfig,
        object secretRefs) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/connections",
            new
            {
                connector_id = connectorId,
                name,
                config = new { base_uri = "http://localhost:5054/sink/erp-auth" },
                destination_authentication = new
                {
                    scheme,
                    config = authConfig,
                    secret_refs = secretRefs
                }
            }));

    protected async Task<Guid> InsertConnectorAsync(
        string key,
        string[] supportedAuthSchemes,
        string direction = "destination")
    {
        Guid id = Guid.NewGuid();
        await using var connection = Fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO connectors (
                id, {{{Fixture.KeyColumn}}}, contract_version, manifest_schema_version, name, direction,
                status, description, manifest, created_at, updated_at)
            VALUES (
                @Id, @Key, 1, 1, @Name, @Direction,
                'active', 'test connector', {{{Fixture.Json("@Manifest")}}}, {{{Fixture.Now}}}, {{{Fixture.Now}}});
            """, new
            {
                Id = id,
                Key = key,
                Name = key,
                Direction = direction,
                Manifest = TestConnectorManifest.Create(key, key, direction, supportedAuthSchemes)
            });

        return id;
    }

    protected async Task<Guid> InsertConnectionAsync(Guid connectorId, string name)
    {
        Guid id = Guid.NewGuid();
        await using var connection = Fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO connections (id, tenant_id, connector_id, name, config, source_verification, destination_authentication, status, environment, description, created_at, updated_at)
            VALUES (@Id, @TenantId, @ConnectorId, @Name, {{{Fixture.Json("@Config")}}}, NULL, NULL, 'active', NULL, NULL, {{{Fixture.Now}}}, {{{Fixture.Now}}});
            """, new { Id = id, Fixture.TenantId, ConnectorId = connectorId, Name = name, Config = "{\"base_uri\":\"http://localhost:5054/sink/erp-auth\"}" });

        return id;
    }

    protected async Task<Guid> GetTenantIdBySlugAsync(string slug)
    {
        await using var connection = Fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<Guid>("SELECT id FROM tenants WHERE slug = @Slug", new { Slug = slug });
    }
}
