using Integrios.Admin.Auth;
using Integrios.Admin.OpenApi;
using Integrios.Application;
using Integrios.Application.ApiKeys;
using Integrios.Application.ApiKeys.Commands;
using Integrios.Application.ApiKeys.Queries;
using Integrios.Application.Tenants;
using Integrios.Application.Tenants.Commands;
using Integrios.Application.Tenants.Queries;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<AdminKeySchemeTransformer>();
});
builder.Services.AddProblemDetails();
builder.Services.AddIntegriosApplication();
builder.Services.AddIntegriosInfrastructure(builder.Configuration);

builder.Services.AddAuthentication(AdminKeyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, AdminKeyAuthHandler>(AdminKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

var admin = app.MapGroup("/admin").RequireAuthorization();

// Tenants — global admin key only

admin.MapPost("/tenants", async (
    CreateTenantRequest request,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.GetAdminPrincipal().IsGlobal)
        return Results.Forbid();

    try
    {
        var response = await mediator.Send(
            new CreateTenantCommand(request.Slug, request.Name, request.Environment, request.Description),
            cancellationToken);
        return Results.Created($"/admin/tenants/{response.Id}", response);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

admin.MapGet("/tenants", async (
    HttpContext httpContext,
    IMediator mediator,
    string? after,
    int limit,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.GetAdminPrincipal().IsGlobal)
        return Results.Forbid();

    limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
    var response = await mediator.Send(new ListTenantsQuery(after, limit), cancellationToken);
    return Results.Ok(response);
});

admin.MapGet("/tenants/{id:guid}", async (
    Guid id,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.GetAdminPrincipal().IsGlobal)
        return Results.Forbid();

    var response = await mediator.Send(new GetTenantByIdQuery(id), cancellationToken);
    return response is null ? Results.NotFound() : Results.Ok(response);
});

admin.MapPatch("/tenants/{id:guid}", async (
    Guid id,
    UpdateTenantRequest request,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.GetAdminPrincipal().IsGlobal)
        return Results.Forbid();

    var response = await mediator.Send(
        new UpdateTenantCommand(id, request.Name, request.Description, request.Environment),
        cancellationToken);

    return response is null ? Results.NotFound() : Results.Ok(response);
});

admin.MapPost("/tenants/{id:guid}/deactivate", async (
    Guid id,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.GetAdminPrincipal().IsGlobal)
        return Results.Forbid();

    var deactivated = await mediator.Send(new DeactivateTenantCommand(id), cancellationToken);
    return deactivated ? Results.Ok() : Results.NotFound();
});

// ApiKeys — global or tenant-scoped admin key (scoped to owning tenant only)

admin.MapPost("/tenants/{tenantId:guid}/api-keys", async (
    Guid tenantId,
    CreateApiKeyRequest request,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    AdminPrincipal principal = httpContext.GetAdminPrincipal();
    if (!principal.IsGlobal && principal.TenantId != tenantId)
        return Results.Forbid();

    CreateApiKeyResponse response = await mediator.Send(
        new CreateApiKeyCommand(tenantId, request.Name, request.Scopes, request.Description, request.ExpiresAt),
        cancellationToken);

    return Results.Created($"/admin/tenants/{tenantId}/api-keys/{response.Key.Id}", response);
});

admin.MapGet("/tenants/{tenantId:guid}/api-keys", async (
    Guid tenantId,
    HttpContext httpContext,
    IMediator mediator,
    string? after,
    int limit,
    CancellationToken cancellationToken) =>
{
    AdminPrincipal principal = httpContext.GetAdminPrincipal();
    if (!principal.IsGlobal && principal.TenantId != tenantId)
        return Results.Forbid();

    limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
    ApiKeyListResponse response = await mediator.Send(
        new ListApiKeysByTenantQuery(tenantId, after, limit), cancellationToken);
    return Results.Ok(response);
});

admin.MapGet("/tenants/{tenantId:guid}/api-keys/{id:guid}", async (
    Guid tenantId,
    Guid id,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    AdminPrincipal principal = httpContext.GetAdminPrincipal();
    if (!principal.IsGlobal && principal.TenantId != tenantId)
        return Results.Forbid();

    ApiKeyResponse? response = await mediator.Send(new GetApiKeyByIdQuery(tenantId, id), cancellationToken);
    return response is null ? Results.NotFound() : Results.Ok(response);
});

admin.MapPost("/tenants/{tenantId:guid}/api-keys/{id:guid}/revoke", async (
    Guid tenantId,
    Guid id,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    AdminPrincipal principal = httpContext.GetAdminPrincipal();
    if (!principal.IsGlobal && principal.TenantId != tenantId)
        return Results.Forbid();

    bool revoked = await mediator.Send(new RevokeApiKeyCommand(tenantId, id), cancellationToken);
    return revoked ? Results.Ok() : Results.NotFound();
});

app.Run();

// Request contracts (Admin-layer only; not shared with Application)

record CreateTenantRequest(string Slug, string Name, string? Environment, string? Description);
record UpdateTenantRequest(string Name, string? Description, string? Environment);
record CreateApiKeyRequest(string Name, IReadOnlyList<string>? Scopes, string? Description, DateTimeOffset? ExpiresAt);
