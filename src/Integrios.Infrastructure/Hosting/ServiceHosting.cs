using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Integrios.Infrastructure.Hosting;

public static class ServiceHosting
{
    // Each integration does nothing unless its service manager started the process.
    // The role names the application because every role shares the integrios entry assembly, and
    // defaults keyed to the application name, such as the Event Log source, must stay per role.
    public static WebApplicationBuilder CreateBuilder(string[] args, string applicationName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = applicationName,
            // A Windows service starts in System32; its settings sit next to the binaries.
            ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
        });
        builder.Services.AddWindowsService();
        builder.Services.AddSystemd();
        return builder;
    }
}
