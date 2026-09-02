using System.Reflection;
using System.Runtime.CompilerServices;
using MediatR;

namespace Integrios.ArchitectureTests;

public sealed class ApplicationArchitectureTests
{
    // Groups, not capabilities: a new capability inside an approved group no longer needs listing
    // here, because PortOwners in HostCompositionArchitectureTests forces explicit host-ownership
    // review for every public port regardless of where it lives. See ADR-0038 (2026-08-24).
    private static readonly string[] ApprovedPortGroups =
    [
        "Integrios.Application.Authoring",
        "Integrios.Application.Identity",
        "Integrios.Application.Ingestion",
        "Integrios.Application.Delivery",
        "Integrios.Application.Secrets",
        "Integrios.Application.Transforms"
    ];

    [Fact]
    public void PublicApplicationPorts_LiveInApprovedResponsibilityGroups()
    {
        Type[] publicPorts = ApplicationAssembly.GetExportedTypes()
            .Where(type => type.IsInterface)
            .ToArray();

        publicPorts.ShouldNotBeEmpty();

        string[] offenders = publicPorts
            .Where(port => !ApprovedPortGroups.Any(group => IsInGroup(port.Namespace, group)))
            .Select(port => port.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        (offenders.Length == 0).ShouldBeTrue(
            "Every public Application port lives in an approved responsibility group. "
            + $"Found: {string.Join(", ", offenders)}");
    }

    internal static bool IsInGroup(string? candidateNamespace, string group) =>
        candidateNamespace is not null
        && (candidateNamespace.Equals(group, StringComparison.Ordinal)
            || candidateNamespace.StartsWith(group + ".", StringComparison.Ordinal));

    [Fact]
    public void ApplicationTypes_AreNeverNamedResponse()
    {
        string[] responseTypes = AuthoredApplicationTypeNames()
            .Where(name => name.EndsWith("Response", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        (responseTypes.Length == 0).ShouldBeTrue(
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

        (requestTypes.Length == 0).ShouldBeTrue(
            "A request is a transport contract owned by a host: route values are authoritative and "
            + "wire deserialization ignores C# nullability, so untrusted input becomes trusted in "
            + $"exactly one place. Application does not know requests exist. Found: {string.Join(", ", requestTypes)}");
    }

    [Fact]
    public void Application_NeverDeclaresGenericResultWrapper()
    {
        string[] offenders = ApplicationAssembly.GetTypes()
            .Where(type => type.IsGenericTypeDefinition)
            .Where(type => GenericNameWithoutArity(type) == "Result")
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        (offenders.Length == 0).ShouldBeTrue(
            "Rule 2: never introduce a generic success-or-failure Result<T> wrapper; every operation "
            + $"gets a specific *Result type. Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void PortInterfaces_DeclareNoDefaultParameterValues()
    {
        string[] offenders = ApplicationAssembly.GetExportedTypes()
            .Where(type => type.IsInterface)
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetParameters()
                .Where(parameter => parameter.HasDefaultValue)
                .Select(parameter => $"{method.DeclaringType!.FullName}.{method.Name}({parameter.Name})"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        (offenders.Length == 0).ShouldBeTrue(
            "Rule 8: port interfaces declare no default parameter values; every call site passes "
            + $"CancellationToken explicitly. Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ApplicationPortSignatures_ReferenceOnlyDomainApplicationOrBcl()
    {
        Assembly domainAssembly = Assembly.Load("Integrios.Domain");

        string[] offenders = ApplicationAssembly.GetExportedTypes()
            .Where(type => type.IsInterface)
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType)
                .Where(signatureType => !IsApprovedSignatureType(signatureType, domainAssembly))
                .Select(signatureType => $"{method.DeclaringType!.FullName}.{method.Name}: {signatureType}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        (offenders.Length == 0).ShouldBeTrue(
            "No exported Application port signature may reference a type outside Domain, Application, "
            + $"or the BCL; a fake implementation must compile with zero packages installed. Found: {string.Join(", ", offenders)}");
    }

    private static bool IsApprovedSignatureType(Type type, Assembly domainAssembly)
    {
        if (type.IsByRef || type.IsArray)
            return IsApprovedSignatureType(type.GetElementType()!, domainAssembly);
        if (type == typeof(void) || type.IsGenericParameter)
            return true;

        // A generic type must be approved on its own definition as well as its arguments. Checking
        // only the arguments lets a package type through whenever they happen to be approved, so
        // MediatR.IRequestHandler<SomeCommand, SomeDto> would read as clean.
        if (type.IsGenericType)
        {
            return IsApprovedAssembly(type.GetGenericTypeDefinition().Assembly, domainAssembly)
                && type.GetGenericArguments().All(argument => IsApprovedSignatureType(argument, domainAssembly));
        }

        return IsApprovedAssembly(type.Assembly, domainAssembly);
    }

    private static bool IsApprovedAssembly(Assembly assembly, Assembly domainAssembly) =>
        assembly == domainAssembly
        || assembly == ApplicationAssembly
        || assembly == typeof(object).Assembly
        || (assembly.GetName().Name?.StartsWith("System.", StringComparison.Ordinal) ?? false);

    private static string GenericNameWithoutArity(Type type) =>
        type.Name.Contains('`', StringComparison.Ordinal)
            ? type.Name[..type.Name.IndexOf('`')]
            : type.Name;

    [Fact]
    public void MediatRHandlers_AreInternal()
    {
        HandlerRegistration[] handlers = HandlerRegistrations().ToArray();

        handlers.ShouldNotBeEmpty();
        foreach (var handler in handlers)
            handler.ImplementationType.IsNotPublic.ShouldBeTrue(
                $"{handler.ImplementationType.FullName} must remain internal.");
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
