using Integrios.Admin.Auth;
using Integrios.Application.ApiKeys;
using Integrios.Application.ApiKeys.Commands;
using Integrios.Application.ApiKeys.Queries;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class ApiKeysEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/api-keys";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateApiKey);
        group.MapGet(ListApiKeys);
        group.MapGet(GetApiKeyById, "/{id:guid}");
        group.MapPost(RevokeApiKey, "/{id:guid}/revoke");
    }

    private static async Task<IResult> CreateApiKey(
        Guid tenantId,
        CreateApiKeyRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        CreateApiKeyResponse response = await mediator.Send(
            new CreateApiKeyCommand(tenantId, request.Name, request.Scopes, request.Description, request.ExpiresAt),
            cancellationToken);
        return Results.Created($"/admin/tenants/{tenantId}/api-keys/{response.Key.Id}", response);
    }

    private static async Task<IResult> ListApiKeys(
        Guid tenantId,
        HttpContext httpContext,
        IMediator mediator,
        string? after,
        int limit,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        ApiKeyListResponse response = await mediator.Send(
            new ListApiKeysByTenantQuery(tenantId, after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetApiKeyById(
        Guid tenantId,
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        ApiKeyResponse? response = await mediator.Send(new GetApiKeyByIdQuery(tenantId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> RevokeApiKey(
        Guid tenantId,
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        bool revoked = await mediator.Send(new RevokeApiKeyCommand(tenantId, id), cancellationToken);
        return revoked ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string>? Scopes, string? Description, DateTimeOffset? ExpiresAt);
