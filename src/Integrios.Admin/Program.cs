using Integrios.Admin.Auth;
using Integrios.Admin.Endpoints;
using Integrios.Admin.OpenApi;
using Integrios.Application;
using Integrios.Infrastructure;
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
admin.MapEndpoints(typeof(Program).Assembly);

app.Run();
