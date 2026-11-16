using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Integrios.Admin.Cli;

/// Writes the OpenAPI document of the composed Admin host without starting it, so the dashboard's
/// typed client is generated from the contract the API serves.
public static class OpenApiCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["openapi", var path, ..])
        {
            Console.Error.WriteLine("Usage: openapi <output-file>");
            return 2;
        }

        (WebApplicationBuilder builder, int operationalPort) = AdminHost.CreateBuilder(args);
        builder.Services.AddSingleton<IServer, NoListenerServer>();
        await using WebApplication app = builder.Build();
        AdminHost.ConfigurePipeline(app, operationalPort);

        // Starting is what attaches the mapped endpoints to the document; nothing listens.
        await app.StartAsync();
        OpenApiDocument document = await app.Services
            .GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
            .GetOpenApiDocumentAsync(CancellationToken.None);
        await app.StopAsync();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using FileStream file = File.Create(path);
        await document.SerializeAsJsonAsync(file, OpenApiSpecVersion.OpenApi3_1);
        return 0;
    }

    private sealed class NoListenerServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
