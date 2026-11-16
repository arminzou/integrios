using Integrios.Admin;
using Integrios.DeliveryApi;
using Integrios.Ingestion;
using Integrios.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Integrios.UnitTests;

public sealed class HostDefaultsTests : IDisposable
{
    private readonly DirectoryInfo keyRing = Directory.CreateTempSubdirectory("integrios-role-defaults-");

    public void Dispose() => keyRing.Delete(recursive: true);

    [Fact]
    public void EachRole_ListensForOperationalTrafficOnItsOwnDefaultPort()
    {
        (string Role, int Port, int Default)[] roles =
        [
            (IngestionHost.Name, IngestionHost.CreateBuilder(Args()).OperationalPort, IngestionHost.DefaultOperationalPort),
            (AdminHost.Name, AdminHost.CreateBuilder(Args()).OperationalPort, AdminHost.DefaultOperationalPort),
            (WorkerHost.Name, WorkerHost.CreateBuilder(Args()).OperationalPort, WorkerHost.DefaultOperationalPort),
            (DeliveryApiHost.Name, DeliveryApiHost.CreateBuilder(Args()).OperationalPort, DeliveryApiHost.DefaultOperationalPort),
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
            ("Integrios.Ingestion", IngestionHost.CreateBuilder(Args()).Builder),
            ("Integrios.Admin", AdminHost.CreateBuilder(Args()).Builder),
            ("Integrios.Worker", WorkerHost.CreateBuilder(Args()).Builder),
            ("Integrios.DeliveryApi", DeliveryApiHost.CreateBuilder(Args()).Builder),
        ];

        foreach ((string expected, WebApplicationBuilder builder) in roles)
            builder.Environment.ApplicationName.ShouldBe(expected);
    }

    [Fact]
    public void DeliveryApi_ListensOnLoopbackUnlessAListenerIsConfigured()
    {
        DeliveryApiHost.CreateBuilder(Args()).Builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey)
            .ShouldBe("http://localhost:5241;http://0.0.0.0:5242");

        DeliveryApiHost.CreateBuilder([.. Args(), "--urls=http://0.0.0.0:5241"]).Builder.WebHost
            .GetSetting(WebHostDefaults.ServerUrlsKey)
            .ShouldBe("http://0.0.0.0:5241;http://0.0.0.0:5242");
    }

    private string[] Args() =>
    [
        "--ConnectionStrings:Postgres=Host=localhost;Database=integrios;Username=integrios;Password=integrios",
        "--Integrios:PublicIngestionBaseUri=https://ingestion.example.test",
        $"--Integrios:Admin:DataProtection:KeyRingPath={keyRing.FullName}"
    ];
}
