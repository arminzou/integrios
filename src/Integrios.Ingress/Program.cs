using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Integrios.Ingress.Auth;
using Integrios.Ingress.Endpoints;
using Integrios.Ingress.ErrorHandling;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<IngressExceptionHandler>();
builder.Services.AddIngressApplicationServices();
builder.Services.AddIngressInfrastructureServices(builder.Configuration);
builder.Services.AddTelemetryServices(builder.Configuration, "integrios-ingress");

builder.Services.AddAuthentication(ApiKeyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapEndpoints(typeof(Program).Assembly);
app.MapPrometheusScrapingEndpoint();

app.Run();
