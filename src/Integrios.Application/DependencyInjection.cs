using Integrios.Application.Delivery;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegriosApplication(this IServiceCollection services)
    {
        services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        services.AddSingleton<RetryPolicy>();

        return services;
    }
}
