using Integrios.Admin;
using Integrios.Admin.Auth;
using Integrios.Admin.OperatorKeys;
using Integrios.Admin.Bootstrap;
using Integrios.Admin.Database;
using Integrios.Admin.Endpoints;
using Integrios.Admin.ErrorHandling;
using Integrios.Admin.OpenApi;
using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json;

if (args is ["bootstrap", ..])
    return await BootstrapCli.RunAsync(args);
if (args is ["operator-key", ..])
    return await OperatorKeyCli.RunAsync(args);
if (args is ["database", ..])
    return await DatabaseMigrationCli.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddOperationalConsoleLogging(builder.Environment.IsDevelopment());
int operationalPort = builder.AddOperationalEndpoints("OperationalPort");

builder.Services.AddSingleton(PublicIngestionBaseUri.Parse(
    builder.Configuration[PublicIngestionBaseUri.ConfigurationKey],
    builder.Environment.IsDevelopment()));

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<OperatorKeySchemeTransformer>();
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AdminExceptionHandler>();
builder.Services.AddAdminApplicationServices();
builder.Services.AddAdminInfrastructureServices(builder.Configuration);
builder.Services.AddTelemetryServices(builder.Configuration, "integrios-admin");

builder.Services.AddAuthentication(OperatorKeyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, OperatorKeyAuthHandler>(OperatorKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseRouting();
app.UseOperationalEndpointIsolation(operationalPort);
app.UseRequestCompletionLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

var admin = app.MapGroup("/admin").RequireAuthorization();
admin.MapEndpoints(typeof(Program).Assembly);

app.MapOperationalEndpoints();

app.Run();
return 0;
