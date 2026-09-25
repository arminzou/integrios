using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application;

public static class DependencyInjection
{
    private const string GroupRoot = "Integrios.Application.";
    private const string Admin = "Admin";
    private const string Ingestion = "Ingestion";
    private const string Worker = "Worker";

    // Operator replay and delivery recovery are Delivery work invoked by Admin.
    private static readonly Dictionary<Type, string> CrossGroupOwners = new()
    {
        [typeof(ReplayEventDeliveryCommandHandler)] = Admin,
        [typeof(GetEventDeliveryRecoveryQueryHandler)] = Admin
    };

    internal static IServiceCollection AddApplicationServices(this IServiceCollection services)
        => AddApplicationServices(services, static _ => true);

    public static IServiceCollection AddAdminApplicationServices(this IServiceCollection services)
        => AddApplicationServices(services, OwnedBy(Admin, "Authoring", "Identity", "EventMonitoring"));

    public static IServiceCollection AddIngestionApplicationServices(this IServiceCollection services)
        => AddApplicationServices(services, OwnedBy(Ingestion, "Ingestion"));

    public static IServiceCollection AddWorkerApplicationServices(this IServiceCollection services)
        => AddApplicationServices(services, OwnedBy(Worker, "Delivery"));

    private static IServiceCollection AddApplicationServices(
        IServiceCollection services,
        Func<Type, bool> handlerFilter)
    {
        services.AddMediatR(configuration =>
        {
            configuration.TypeEvaluator = handlerFilter;
            configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            // Outermost behavior: wraps every handler in a span before any other pipeline step.
            configuration.AddOpenBehavior(typeof(TelemetryBehavior<,>));
        });

        services.AddMetrics();
        services.AddSingleton<IntegriosMetrics>();

        return services;
    }

    private static Func<Type, bool> OwnedBy(string host, params string[] groups)
    {
        string[] prefixes = [.. groups.Select(group => GroupRoot + group)];

        return type => CrossGroupOwners.TryGetValue(type, out string? owner)
            ? owner == host
            : IsInGroup(type, prefixes);
    }

    private static bool IsInGroup(Type type, string[] prefixes) =>
        type.Namespace is string typeNamespace
        && prefixes.Any(prefix =>
            typeNamespace.Equals(prefix, StringComparison.Ordinal)
            || typeNamespace.StartsWith(prefix + ".", StringComparison.Ordinal));
}
