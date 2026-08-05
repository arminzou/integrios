using Integrios.Admin;
using Integrios.Admin.Auth;
using Integrios.Admin.AdminKeys;
using Integrios.Admin.Bootstrap;
using Integrios.Admin.Endpoints;
using Integrios.Admin.ErrorHandling;
using Integrios.Admin.OpenApi;
using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json;

if (args is ["bootstrap", ..])
    return await BootstrapCli.RunAsync(args);
if (args is ["admin-key", ..])
    return await AdminKeyCli.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(PublicIngressBaseUri.Parse(
    builder.Configuration[PublicIngressBaseUri.ConfigurationKey],
    builder.Environment.IsDevelopment()));

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<AdminKeySchemeTransformer>();
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AdminExceptionHandler>();
builder.Services.AddAdminApplicationServices();
builder.Services.AddAdminInfrastructureServices(builder.Configuration);
builder.Services.AddTelemetryServices(builder.Configuration, "integrios-admin");

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
admin.MapEndpoints(typeof(Program).Assembly);

app.MapPrometheusScrapingEndpoint();

app.Run();
return 0;
