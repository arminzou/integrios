using System.Net.Http.Headers;
using Integrios.Client;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class SubscriberServiceCollectionExtensions
{
    /// Consumes one pull Destination with <typeparamref name="THandler"/>, resolved in a new scope
    /// for each delivery. Returns the HTTP client's builder, to add handlers or resilience.
    public static IHttpClientBuilder AddIntegriosSubscriber<THandler>(
        this IServiceCollection services, Action<SubscriberOptions> configure)
        where THandler : class, IDeliveryHandler
    {
        string name = typeof(THandler).FullName!;
        services.AddOptions<SubscriberOptions>(name)
            .Configure(configure)
            .Validate(options => options.BaseAddress is { IsAbsoluteUri: true }, "BaseAddress must be an absolute URI.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.SubscriberKey), "SubscriberKey is required.")
            .Validate(options => options.Concurrency >= 1, "Concurrency must be at least 1.")
            .ValidateOnStart();
        services.TryAddScoped<THandler>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService(provider => new DeliveryLoop<THandler>(
            new HttpDeliveryTransport(provider.GetRequiredService<IHttpClientFactory>().CreateClient(name)),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<SubscriberOptions>>().Get(name).Concurrency,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<DeliveryLoop<THandler>>>()));

        return services.AddHttpClient(name, (provider, client) =>
        {
            SubscriberOptions options = provider.GetRequiredService<IOptionsMonitor<SubscriberOptions>>().Get(name);
            string address = options.BaseAddress!.ToString();
            // Without a trailing slash, relative request paths would replace the base path's last segment.
            client.BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SubscriberKey);
        }).UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2));
    }
}
