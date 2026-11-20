using Integrios.Admin;
using Integrios.Ingestion;
using Integrios.Subscriber;
using Integrios.Worker;

namespace Integrios.UnitTests;

public sealed class HostSelectorTests
{
    [Theory]
    [InlineData]
    [InlineData("delivery")]
    [InlineData("Worker")]
    [InlineData("bootstrap")]
    [InlineData("--role=worker")]
    public async Task WithoutAKnownRoleFirst_PrintsTheRolesAndExitsTwo(params string[] args)
    {
        var error = new StringWriter();

        int exitCode = await HostSelector.RunAsync(args, error);

        exitCode.ShouldBe(2);
        error.ToString().ShouldContain("Roles: ingestion, admin, worker, subscriber");
    }

    [Theory]
    [InlineData(new[] { "worker", "secrets", "validate", "--all" }, "worker", new[] { "secrets", "validate", "--all" })]
    [InlineData(new[] { "admin", "bootstrap" }, "admin", new[] { "bootstrap" })]
    [InlineData(new[] { "ingestion" }, "ingestion", new string[0])]
    [InlineData(new[] { "subscriber", "--OperationalPort=6000" }, "subscriber", new[] { "--OperationalPort=6000" })]
    public async Task AKnownRole_RunsThatRoleWithTheArgumentsAfterIt(
        string[] args, string expectedRole, string[] expectedArgs)
    {
        string? ranRole = null;
        string[]? ranWith = null;
        var roles = HostSelector.Roles.Keys.ToDictionary(
            name => name,
            name => (Func<string[], Task<int>>)(roleArgs =>
            {
                ranRole = name;
                ranWith = roleArgs;
                return Task.FromResult(7);
            }));

        int exitCode = await HostSelector.RunAsync(args, roles, TextWriter.Null);

        exitCode.ShouldBe(7);
        ranRole.ShouldBe(expectedRole);
        ranWith.ShouldBe(expectedArgs);
    }

    [Fact]
    public void EachRoleName_StartsItsOwnRole()
    {
        HostSelector.Roles.ShouldBe(new Dictionary<string, Func<string[], Task<int>>>
        {
            ["ingestion"] = IngestionHost.RunAsync,
            ["admin"] = AdminHost.RunAsync,
            ["worker"] = WorkerHost.RunAsync,
            ["subscriber"] = SubscriberHost.RunAsync,
        }, ignoreOrder: true);
    }
}
