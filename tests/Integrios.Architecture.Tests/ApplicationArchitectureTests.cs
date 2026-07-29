using System.Reflection;
using MediatR;

namespace Integrios.Architecture.Tests;

public sealed class ApplicationArchitectureTests
{
    private static readonly HashSet<string> ApprovedPortNamespaces =
    [
        "Integrios.Application.AdminKeys",
        "Integrios.Application.ApiKeys",
        "Integrios.Application.Auth",
        "Integrios.Application.Connections",
        "Integrios.Application.Delivery",
        "Integrios.Application.Events",
        "Integrios.Application.Integrations",
        "Integrios.Application.Outbox",
        "Integrios.Application.Secrets",
        "Integrios.Application.Subscriptions",
        "Integrios.Application.Tenants",
        "Integrios.Application.Topics",
        "Integrios.Application.Transforms"
    ];

    [Fact]
    public void PublicApplicationPorts_LiveInApprovedCapabilityNamespaces()
    {
        Type[] publicPorts = ApplicationAssembly.GetExportedTypes()
            .Where(type => type.IsInterface)
            .ToArray();

        Assert.NotEmpty(publicPorts);
        Assert.All(publicPorts, port => Assert.Contains(port.Namespace ?? string.Empty, ApprovedPortNamespaces));
    }

    [Fact]
    public void MediatRHandlers_AreInternal()
    {
        HandlerRegistration[] handlers = HandlerRegistrations().ToArray();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, handler => Assert.True(
            handler.ImplementationType.IsNotPublic,
            $"{handler.ImplementationType.FullName} must remain internal."));
    }

    internal static Assembly ApplicationAssembly => Assembly.Load("Integrios.Application");

    internal static IEnumerable<HandlerRegistration> HandlerRegistrations() =>
        ApplicationAssembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(implementationType => implementationType
                .GetInterfaces()
                .Where(IsHandlerInterface)
                .Select(serviceType => new HandlerRegistration(implementationType, serviceType)));

    internal static bool IsHandlerInterface(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(IRequestHandler<>)
            || type.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
            || type.GetGenericTypeDefinition() == typeof(INotificationHandler<>)
            || type.GetGenericTypeDefinition() == typeof(IStreamRequestHandler<,>));

    internal sealed record HandlerRegistration(Type ImplementationType, Type ServiceType);
}
