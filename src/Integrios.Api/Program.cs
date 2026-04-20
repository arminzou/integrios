using Integrios.Api.Auth;
using Integrios.Api.Infrastructure.Data;
using Integrios.Api.Infrastructure.Data.Events;
using Integrios.Api.Infrastructure.Data.Tenants;
using Integrios.Core.Contracts;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnectionString))
    throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddSingleton(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddSingleton<IApiCredentialRepository, ApiCredentialRepository>();
builder.Services.AddSingleton<IEventIngestionRepository, EventIngestionRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

var events = app.MapGroup("/events");
events.AddEndpointFilter<ApiKeyEndpointFilter>();
events.MapPost("", async (
    IngestEventRequest request,
    HttpContext httpContext,
    IEventIngestionRepository ingestionRepository,
    CancellationToken cancellationToken) =>
{
    var tenantContext = httpContext.GetTenantContext();
    var response = await ingestionRepository.IngestAsync(
        tenantContext.Tenant.Id,
        request,
        cancellationToken);

    return Results.Accepted(value: response);
});

app.Run();
