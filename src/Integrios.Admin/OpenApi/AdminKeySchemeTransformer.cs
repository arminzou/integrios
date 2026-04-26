using Integrios.Admin.Auth;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Integrios.Admin.OpenApi;

public sealed class AdminKeySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        var schemes = document.Components.SecuritySchemes;
        if (schemes is null)
            return Task.CompletedTask;
        schemes[AdminKeyAuthHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "Admin key credentials. Format: `AdminKey <publicKey>:<secret>`",
        };

        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(AdminKeyAuthHandler.SchemeName, document)] = [],
        };

        foreach (var path in document.Paths.Values)
        {
            foreach (var operation in (path.Operations ?? []).Values)
            {
                operation.Security ??= [];
                operation.Security.Add(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
