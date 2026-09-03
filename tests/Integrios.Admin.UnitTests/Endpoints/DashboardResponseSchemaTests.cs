using Integrios.Admin.Endpoints;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Integrios.Admin.UnitTests.Endpoints;

/// An endpoint that does not declare its response schema is emitted into the OpenAPI document with
/// no response type, so the generated browser client reads it as untyped and stops failing when the
/// contract changes shape. Every route the dashboard reads is pinned here.
public sealed class DashboardResponseSchemaTests
{
    private static readonly string[] ReadRoutes =
    [
        "/tenants",
        "/tenants/{id:guid}",
        "/connectors",
        "/connectors/{id:guid}",
        "/tenants/{tenantId:guid}/connections",
        "/tenants/{tenantId:guid}/connections/{id:guid}",
        "/tenants/{tenantId:guid}/tenant-api-keys",
        "/tenants/{tenantId:guid}/tenant-api-keys/{id:guid}",
        "/tenants/{tenantId:guid}/sources",
        "/tenants/{tenantId:guid}/sources/{id:guid}",
        "/tenants/{tenantId:guid}/topics",
        "/tenants/{tenantId:guid}/topics/{id:guid}",
        "/tenants/{tenantId:guid}/topics/{topicId:guid}/subscriptions",
        "/tenants/{tenantId:guid}/topics/{topicId:guid}/subscriptions/{id:guid}",
        "/tenants/{tenantId:guid}/events",
        "/tenants/{tenantId:guid}/events/{eventId:guid}/deliveries",
    ];

    [Fact]
    public void EveryRouteTheDashboardReads_DeclaresItsResponseSchema()
    {
        IReadOnlyList<RouteEndpoint> endpoints = MapAdminEndpoints();

        foreach (string route in ReadRoutes)
        {
            RouteEndpoint? endpoint = endpoints.SingleOrDefault(candidate =>
                Normalize(candidate.RoutePattern.RawText) == route
                && candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);

            endpoint.ShouldNotBeNull($"No GET endpoint is mapped for {route}.");
            DeclaresASuccessSchema(endpoint).ShouldBeTrue(
                $"GET {route} declares no response schema, so the generated client reads it as untyped.");
        }
    }

    [Fact]
    public void EveryCreateTheDashboardReadsBack_DeclaresItsCreatedSchema()
    {
        IReadOnlyList<RouteEndpoint> endpoints = MapAdminEndpoints();
        string[] createRoutes =
        [
            "/tenants",
            "/tenants/{tenantId:guid}/connections",
            "/tenants/{tenantId:guid}/tenant-api-keys",
            "/tenants/{tenantId:guid}/sources",
            "/tenants/{tenantId:guid}/topics",
            "/tenants/{tenantId:guid}/topics/{topicId:guid}/subscriptions",
        ];

        foreach (string route in createRoutes)
        {
            RouteEndpoint endpoint = endpoints.Single(candidate =>
                Normalize(candidate.RoutePattern.RawText) == route
                && candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);

            // The dashboard navigates to what it just created, so the created resource has to arrive
            // as a typed body rather than only as a Location header.
            endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Any(metadata => metadata.StatusCode == StatusCodes.Status201Created && metadata.Type is not null)
                .ShouldBeTrue($"POST {route} declares no 201 response schema.");
        }
    }

    // A group's collection route is mapped with an empty pattern, so it materialises with a
    // trailing slash the declared routes do not carry.
    private static string? Normalize(string? pattern) =>
        pattern is not null && pattern.Length > 1 ? pattern.TrimEnd('/') : pattern;

    private static bool DeclaresASuccessSchema(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Any(metadata => metadata.StatusCode == StatusCodes.Status200OK && metadata.Type is not null);

    private static IReadOnlyList<RouteEndpoint> MapAdminEndpoints()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        // Routing infers each handler's metadata as it maps, which means it has to recognise the
        // handler's own services. Nothing here is ever invoked: no request is served.
        builder.Services.AddSingleton<IMediator>(_ => throw new NotSupportedException("Mapping only."));

        WebApplication app = builder.Build();
        app.MapEndpoints(typeof(TenantsEndpoints).Assembly);

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }
}
