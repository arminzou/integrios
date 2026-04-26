using Integrios.Admin.Auth;
using Integrios.Application;
using Integrios.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.Configure<AdminAuthOptions>(
    builder.Configuration.GetSection(AdminAuthOptions.SectionName));
builder.Services.AddSingleton<AdminTokenEndpointFilter>();
builder.Services.AddIntegriosApplication();
builder.Services.AddIntegriosInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

var admin = app.MapGroup("/admin");
admin.AddEndpointFilter<AdminTokenEndpointFilter>();

app.Run();
