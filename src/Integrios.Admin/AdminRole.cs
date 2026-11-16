using System.Text.Json;
using Integrios.Admin.Auth;
using Integrios.Admin.Cli;
using Integrios.Admin.Dashboard;
using Integrios.Admin.Endpoints;
using Integrios.Admin.ErrorHandling;
using Integrios.Admin.OpenApi;
using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Infrastructure.Telemetry;

namespace Integrios.Admin;

public static class AdminRole
{
    public const string Name = "admin";
    public const int DefaultOperationalPort = 5151;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["bootstrap", ..])
            return await BootstrapCli.RunAsync(args);
        if (args is ["operator-key", ..])
            return await OperatorKeyCli.RunAsync(args);
        if (args is ["operator-user", ..])
            return await OperatorUserCli.RunAsync(args);
        if (args is ["database", ..])
            return await DatabaseCli.RunAsync(args);

        (WebApplicationBuilder builder, int operationalPort) = CreateBuilder(args);
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
        app.UseMiddleware<OperatorAntiforgeryMiddleware>();
        app.UseAuthorization();

        app.MapOperatorSessionEndpoints();

        var admin = app.MapGroup("/admin").RequireAuthorization(OperatorAuthentication.PolicyName);
        admin.MapEndpoints(typeof(AdminRole).Assembly);

        app.MapOperationalEndpoints();
        app.MapDashboard();

        await app.RunAsync();
        return 0;
    }

    public static (WebApplicationBuilder Builder, int OperationalPort) CreateBuilder(string[] args)
    {
        var builder = ServiceHosting.CreateBuilder(args, Name, typeof(AdminRole).Assembly.GetName().Name!);
        builder.Logging.AddOperationalConsoleLogging(builder.Environment.IsDevelopment());
        int operationalPort = builder.AddOperationalEndpoints(DefaultOperationalPort);

        builder.Services.AddSingleton(PublicIngestionBaseUri.Parse(
            builder.Configuration[PublicIngestionBaseUri.ConfigurationKey],
            builder.Environment.IsDevelopment()));
        builder.Services.AddSingleton(TraceUrlTemplate.Parse(builder.Configuration[TraceUrlTemplate.ConfigurationKey]));

        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<OperatorKeySchemeTransformer>();
            options.AddSchemaTransformer<EventStatusSchemaTransformer>();
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<AdminExceptionHandler>();
        builder.Services.AddAdminApplicationServices();
        builder.Services.AddAdminInfrastructureServices(builder.Configuration);
        builder.Services.AddAdminDataProtection(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddTelemetryServices(builder.Configuration, "integrios-admin");

        builder.Services.AddOperatorAuthentication(builder.Configuration);
        // Matches Integrios:Admin:Oidc:RequireHttpsMetadata's own default: secure-only except for a local
        // plaintext-HTTP deployment that explicitly opted out.
        bool requireHttpsCookies = builder.Configuration.GetValue<bool?>(
            OperatorOidcOptions.SectionKey + ":RequireHttpsMetadata") ?? true;
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-Integrios-Antiforgery";
            options.Cookie.Name = "integrios_antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = requireHttpsCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        return (builder, operationalPort);
    }
}
