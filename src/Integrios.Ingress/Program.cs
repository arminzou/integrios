using Integrios.Application;
using Integrios.Application.Events;
using Integrios.Domain.Contracts;
using Integrios.Infrastructure;
using Integrios.Ingress.Auth;
using MediatR;

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
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var tenantContext = httpContext.GetTenantContext();
    var response = await mediator.Send(
        new IngestEventCommand(tenantContext.Tenant.Id, request),
        cancellationToken);

    return Results.Accepted($"/events/{response.EventId}", response);
});
events.MapGet("/{id:guid}", async (
    Guid id,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var tenantContext = httpContext.GetTenantContext();
    var response = await mediator.Send(
        new GetEventByIdQuery(tenantContext.Tenant.Id, id),
        cancellationToken);

    return response is null ? Results.NotFound() : Results.Ok(response);
});
events.MapPost("/{id:guid}/replay", async (
    Guid id,
    HttpContext httpContext,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var tenantContext = httpContext.GetTenantContext();
    var replayed = await mediator.Send(
        new ReplayEventCommand(tenantContext.Tenant.Id, id),
        cancellationToken);

    return replayed ? Results.Accepted($"/events/{id}") : Results.NotFound();
});

app.Run();
