using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Ingress.Auth;
using Integrios.Ingress.Endpoints;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddIntegriosApplication();
builder.Services.AddIntegriosInfrastructure(builder.Configuration);

builder.Services.AddAuthentication(ApiKeyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapEndpoints(typeof(Program).Assembly);

app.Run();
