using Integrios.Api.Auth;
using Integrios.Api.Infrastructure.Data;
using Integrios.Api.Infrastructure.Data.Tenants;
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

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

var events = app.MapGroup("/events");
events.AddEndpointFilter<ApiKeyEndpointFilter>();
events.MapPost("", () => Results.Accepted()); // placeholder — replaced when intake handler is wired

app.Run();
