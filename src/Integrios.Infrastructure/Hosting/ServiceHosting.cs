using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Integrios.Infrastructure.Hosting;

public static class ServiceHosting
{
    // Each integration does nothing unless its service manager started the process.
    // The role names the application because every role shares the integrios entry assembly, and
    // defaults keyed to the application name must stay per role.
    public static WebApplicationBuilder CreateBuilder(string[] args, string role, string applicationName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = applicationName,
            // A Windows service starts in System32; its settings sit next to the binaries.
            ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
        });
        builder.AddRoleSettingsFiles(role);
        builder.Services.AddWindowsService();
        builder.Services.AddSystemd();
        return builder;
    }

    public static void AddRoleSettingsFiles(this IHostApplicationBuilder builder, string role)
    {
        IList<IConfigurationSource> sources = builder.Configuration.Sources;
        string environment = builder.Environment.EnvironmentName;
        InsertAfter(sources, "appsettings.json", $"appsettings.{role}.json");
        InsertAfter(sources, $"appsettings.{environment}.json", $"appsettings.{role}.{environment}.json");
    }

    private static void InsertAfter(IList<IConfigurationSource> sources, string existingPath, string path)
    {
        int index = -1;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource json
                && string.Equals(json.Path, existingPath, StringComparison.OrdinalIgnoreCase))
                index = i;
        }

        if (index < 0)
            throw new InvalidOperationException(
                $"The host has no {existingPath} source to place {path} after; role settings depend on the default sources.");

        var anchor = (JsonConfigurationSource)sources[index];
        sources.Insert(index + 1, new JsonConfigurationSource
        {
            Path = path,
            Optional = true,
            ReloadOnChange = anchor.ReloadOnChange,
            FileProvider = anchor.FileProvider,
        });
    }
}
