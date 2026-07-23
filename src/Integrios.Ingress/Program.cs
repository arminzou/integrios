using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Integrios.Ingress.Auth;
using Integrios.Ingress.Endpoints;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddIntegriosApplication();
builder.Services.AddIntegriosInfrastructure(builder.Configuration);
builder.Services.AddIntegriosTelemetry(builder.Configuration, "integrios-ingress");

builder.Services.AddAuthentication(ApiKeyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapEndpoints(typeof(Program).Assembly);
app.MapPrometheusScrapingEndpoint();

app.Run();
