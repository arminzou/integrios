using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Integrios.Infrastructure.Hosting;

public static class ServiceHosting
{
    // Each integration does nothing unless its service manager started the process.
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // A Windows service starts in System32; its settings sit next to the binaries.
            ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
        });
        builder.Services.AddWindowsService();
        builder.Services.AddSystemd();
        return builder;
    }
}
