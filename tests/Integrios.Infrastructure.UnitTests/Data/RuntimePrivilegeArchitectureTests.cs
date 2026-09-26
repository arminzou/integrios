using Integrios.Domain.Entities;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Infrastructure.UnitTests.Data;

public sealed class RuntimePrivilegeArchitectureTests
{
    [Fact]
    public void EveryOperatorAuthenticationEntity_IsExcludedFromTheDataPlaneScope()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=integrios;Username=integrios;Password=integrios",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAdminInfrastructureServices(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IntegriosDbContext context = scope.ServiceProvider.GetRequiredService<IntegriosDbContext>();

        string[] operatorAuthenticationTables =
        [
            context.Model.FindEntityType(typeof(OperatorKey))!.GetTableName()!,
            context.Model.FindEntityType(typeof(OperatorIdentity))!.GetTableName()!,
            context.Model.FindEntityType(typeof(PasswordCredential))!.GetTableName()!,
            context.Model.FindEntityType(typeof(User))!.GetTableName()!,
        ];

        operatorAuthenticationTables.Order(StringComparer.Ordinal).ShouldBe(
            RuntimePrincipalScopes.OperatorAuthenticationTables.Order(StringComparer.Ordinal));
    }
}
