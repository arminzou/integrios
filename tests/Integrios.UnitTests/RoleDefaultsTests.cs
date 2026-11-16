using Integrios.Admin;
using Integrios.Ingestion;
using Integrios.Worker;
using Microsoft.AspNetCore.Builder;

namespace Integrios.UnitTests;

public sealed class RoleDefaultsTests : IDisposable
{
    private readonly DirectoryInfo keyRing = Directory.CreateTempSubdirectory("integrios-role-defaults-");

    public void Dispose() => keyRing.Delete(recursive: true);

    [Fact]
    public void EachRole_ListensForOperationalTrafficOnItsOwnDefaultPort()
    {
        (string Role, int Port, int Default)[] roles =
        [
            (IngestionRole.Name, IngestionRole.CreateBuilder(Args()).OperationalPort, IngestionRole.DefaultOperationalPort),
            (AdminRole.Name, AdminRole.CreateBuilder(Args()).OperationalPort, AdminRole.DefaultOperationalPort),
            (WorkerRole.Name, WorkerRole.CreateBuilder(Args()).OperationalPort, WorkerRole.DefaultOperationalPort),
        ];

        foreach ((string role, int port, int defaultPort) in roles)
            port.ShouldBe(defaultPort, $"{role} must use its own default when OperationalPort is not set.");
        roles.Select(role => role.Port).Distinct().Count().ShouldBe(roles.Length);
    }

    [Fact]
    public void EachRole_NamesItsOwnApplication()
    {
        (string Expected, WebApplicationBuilder Builder)[] roles =
        [
            ("Integrios.Ingestion", IngestionRole.CreateBuilder(Args()).Builder),
            ("Integrios.Admin", AdminRole.CreateBuilder(Args()).Builder),
            ("Integrios.Worker", WorkerRole.CreateBuilder(Args()).Builder),
        ];

        foreach ((string expected, WebApplicationBuilder builder) in roles)
            builder.Environment.ApplicationName.ShouldBe(expected);
    }

    private string[] Args() =>
    [
        "--ConnectionStrings:Postgres=Host=localhost;Database=integrios;Username=integrios;Password=integrios",
        "--Integrios:PublicIngestionBaseUri=https://ingestion.example.test",
        $"--Integrios:Admin:DataProtection:KeyRingPath={keyRing.FullName}"
    ];
}
