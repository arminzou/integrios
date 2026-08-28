using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Integrios.Ingestion.Auth;
using Integrios.Ingestion.Endpoints;
using Integrios.Ingestion.ErrorHandling;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<IngestionExceptionHandler>();
builder.Services.AddIngestionApplicationServices();
builder.Services.AddIngestionInfrastructureServices(builder.Configuration);
builder.Services.AddSourceVerificationSecretResolutionServices(builder.Configuration);
builder.Services.AddTelemetryServices(builder.Configuration, "integrios-ingestion");

builder.Services.AddAuthentication(TenantApiKeyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TenantApiKeyAuthHandler>(TenantApiKeyAuthHandler.SchemeName, _ => { });
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
app.MapEndpoints(typeof(Program).Assembly);
app.MapPrometheusScrapingEndpoint();

app.Run();
