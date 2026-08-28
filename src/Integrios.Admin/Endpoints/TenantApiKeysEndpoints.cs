using Integrios.Application.Authoring.TenantApiKeys;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class TenantApiKeysEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/tenant-api-keys";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateTenantApiKey);
        group.MapGet(ListTenantApiKeys);
        group.MapGet(GetTenantApiKeyById, "/{id:guid}");
        group.MapPost(RevokeTenantApiKey, "/{id:guid}/revoke");
    }

    private static async Task<IResult> CreateTenantApiKey(
        Guid tenantId,
        CreateTenantApiKeyRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        CreateTenantApiKeyResult response = await mediator.Send(
            new CreateTenantApiKeyCommand(tenantId, request.Name, request.Description, request.ExpiresAt),
            cancellationToken);
        return Results.Created($"/admin/tenants/{tenantId}/tenant-api-keys/{response.TenantApiKey.Id}", response);
    }

    private static async Task<IResult> ListTenantApiKeys(
        Guid tenantId,
        IMediator mediator,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        TenantApiKeyListDto response = await mediator.Send(
            new ListTenantApiKeysByTenantQuery(tenantId, after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetTenantApiKeyById(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        TenantApiKeyDto? response = await mediator.Send(new GetTenantApiKeyByIdQuery(tenantId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> RevokeTenantApiKey(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        bool revoked = await mediator.Send(new RevokeTenantApiKeyCommand(tenantId, id), cancellationToken);
        return revoked ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateTenantApiKeyRequest(string? Name, string? Description, DateTimeOffset? ExpiresAt);
