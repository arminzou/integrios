using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Domain.Contracts;
using Integrios.Infrastructure.Extensions;
using Integrios.Ingress.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddIntegriosApplication();
builder.Services.AddIntegriosInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

var events = app.MapGroup("/events");
events.AddEndpointFilter<ApiKeyEndpointFilter>();
events.MapPost("", async (
    IngestEventRequest request,
    HttpContext httpContext,
    IEventRepository eventRepository,
    CancellationToken cancellationToken) =>
{
    var tenantContext = httpContext.GetTenantContext();
    var response = await eventRepository.IngestAsync(
        tenantContext.Tenant.Id,
        request,
        cancellationToken);

    return Results.Accepted($"/events/{response.EventId}", response);
});
events.MapGet("/{id:guid}", async (
    Guid id,
    HttpContext httpContext,
    IEventRepository eventRepository,
    CancellationToken cancellationToken) =>
{
    var tenantContext = httpContext.GetTenantContext();
    var response = await eventRepository.GetEventByIdAsync(
        tenantContext.Tenant.Id,
        id,
        cancellationToken);

    return response is null ? Results.NotFound() : Results.Ok(response);
});
events.MapPost("/{id:guid}/replay", async (
    Guid id,
    HttpContext httpContext,
    IEventRepository eventRepository,
    CancellationToken cancellationToken) =>
{
    var tenantContext = httpContext.GetTenantContext();
    var replayed = await eventRepository.ReplayEventAsync(
        tenantContext.Tenant.Id,
        id,
        cancellationToken);

    return replayed ? Results.Accepted($"/events/{id}") : Results.NotFound();
});

app.Run();
