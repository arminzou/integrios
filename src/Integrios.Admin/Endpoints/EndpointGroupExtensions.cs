using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Integrios.Admin.Endpoints;

public static class EndpointGroupExtensions
{
    public static void MapEndpoints(this IEndpointRouteBuilder app, Assembly assembly)
    {
        var groups = assembly.GetTypes()
            .Where(t => typeof(IEndpointGroup).IsAssignableFrom(t) && t is { IsAbstract: false, IsClass: true })
            .Select(t => (IEndpointGroup)Activator.CreateInstance(t)!);

        foreach (var endpoint in groups)
            endpoint.Map(app.MapGroup(endpoint.Prefix));
    }

    public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern = "")
    {
        GuardAnonymous(handler);
        return builder.MapGet(pattern, handler).WithName(handler.Method.Name);
    }

    public static RouteHandlerBuilder MapPost(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern = "")
    {
        GuardAnonymous(handler);
        return builder.MapPost(pattern, handler).WithName(handler.Method.Name);
    }

    public static RouteHandlerBuilder MapPut(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern)
    {
        GuardAnonymous(handler);
        return builder.MapPut(pattern, handler).WithName(handler.Method.Name);
    }

    public static RouteHandlerBuilder MapPatch(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern)
    {
        GuardAnonymous(handler);
        return builder.MapPatch(pattern, handler).WithName(handler.Method.Name);
    }

    public static RouteHandlerBuilder MapDelete(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern)
    {
        GuardAnonymous(handler);
        return builder.MapDelete(pattern, handler).WithName(handler.Method.Name);
    }

    private static void GuardAnonymous(Delegate handler)
    {
        if (handler.Method.Name.StartsWith('<'))
            throw new ArgumentException("Anonymous methods cannot be used as endpoint handlers — use a named method so WithName can derive the endpoint name.", nameof(handler));
    }
}
