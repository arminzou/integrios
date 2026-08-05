using System.Reflection;
using System.Runtime.CompilerServices;
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
    public void ApplicationTypes_AreNeverNamedResponse()
    {
        string[] responseTypes = AuthoredApplicationTypeNames()
            .Where(name => name.EndsWith("Response", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            responseTypes.Length == 0,
            "A response is a transport contract owned by a host. Application projects persisted "
            + $"state as *Dto and describes outcomes as *Result or *Report. Found: {string.Join(", ", responseTypes)}");
    }

    [Fact]
    public void ApplicationTypes_CarryNoRequestVocabulary()
    {
        // Contains rather than EndsWith, so this covers the wire record itself, the exception
        // names that used to say RequestValidation, and anything like a *RequestValidator.
        string[] requestTypes = AuthoredApplicationTypeNames()
            .Where(name => name.Contains("Request", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            requestTypes.Length == 0,
            "A request is a transport contract owned by a host: route values are authoritative and "
            + "wire deserialization ignores C# nullability, so untrusted input becomes trusted in "
            + $"exactly one place. Application does not know requests exist. Found: {string.Join(", ", requestTypes)}");
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

    // Async state machines and closures inherit the name of the method they were generated for,
    // so a method like BuildRequestDecoratorAsync would otherwise read as a banned type name.
    private static IEnumerable<string> AuthoredApplicationTypeNames() =>
        ApplicationAssembly.GetTypes()
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Select(type => type.Name);

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
