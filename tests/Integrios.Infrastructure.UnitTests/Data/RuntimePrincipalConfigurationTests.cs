using Integrios.Infrastructure.Data;
using Microsoft.Extensions.Configuration;

namespace Integrios.Infrastructure.UnitTests.Data;

public sealed class RuntimePrincipalConfigurationTests
{
    [Fact]
    public void Read_AcceptsPasswordGrantOnlyAndProviderSpecificEntraCredentials()
    {
        IReadOnlyList<RuntimePrincipal> postgres = Read(DatabaseProvider.Postgres,
            ("Database:RuntimePrincipals:0:Name", "admin_runtime"),
            ("Database:RuntimePrincipals:0:Scope", "control-plane"),
            ("Database:RuntimePrincipals:0:Password", "runtime-password"),
            ("Database:RuntimePrincipals:1:Name", "worker_runtime"),
            ("Database:RuntimePrincipals:1:Scope", "data-plane"),
            ("Database:RuntimePrincipals:2:Name", "azure_runtime"),
            ("Database:RuntimePrincipals:2:Scope", "data-plane"),
            ("Database:RuntimePrincipals:2:EntraClientId", "1f658b22-0c7d-45e7-99a5-36b86cbb2634"),
            ("Database:RuntimePrincipals:2:EntraObjectId", "b81b1ff7-ffb1-4137-96f9-dd9a94a32512"));

        postgres.Count.ShouldBe(3);
        postgres[0].Password.ShouldBe("runtime-password");
        postgres[0].Scope.ShouldBe(RuntimePrincipalScope.ControlPlane);
        postgres[1].Password.ShouldBeNull();
        postgres[2].EntraClientId.ShouldBe(Guid.Parse("1f658b22-0c7d-45e7-99a5-36b86cbb2634"));
        postgres[2].EntraObjectId.ShouldBe(Guid.Parse("b81b1ff7-ffb1-4137-96f9-dd9a94a32512"));

        RuntimePrincipal sqlEntra = Read(DatabaseProvider.SqlServer,
            ("Database:RuntimePrincipals:0:Name", "sql_runtime"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:0:EntraClientId", "1f658b22-0c7d-45e7-99a5-36b86cbb2634"))[0];
        sqlEntra.EntraClientId.ShouldNotBeNull();
    }

    [Fact]
    public void Read_RejectsEmptyListAndPresentButEmptyCredentials()
    {
        InvalidOperationException empty = Should.Throw<InvalidOperationException>(() =>
            RuntimePrincipalConfiguration.Read(Configuration([]), DatabaseProvider.Postgres));
        empty.Message.ShouldContain("Database:RuntimePrincipals");

        InvalidOperationException credential = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.Postgres,
            ("Database:RuntimePrincipals:0:Name", "worker"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:0:Password", "")));
        credential.Message.ShouldContain("Database:RuntimePrincipals:0:Password");
        credential.Message.ShouldNotContain("\"\"");
    }

    [Fact]
    public void Read_RejectsSqlServerPasswordLongerThanTheServerAccepts()
    {
        string password = new('p', 129);
        InvalidOperationException tooLong = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.SqlServer,
            ("Database:RuntimePrincipals:0:Name", "worker"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:0:Password", password)));
        tooLong.Message.ShouldContain("Database:RuntimePrincipals:0:Password");
        tooLong.Message.ShouldNotContain(password);

        Read(DatabaseProvider.Postgres,
            ("Database:RuntimePrincipals:0:Name", "worker"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:0:Password", password)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("pass;word")]
    [InlineData("pass'word")]
    [InlineData("pass\"word")]
    [InlineData("pass word")]
    [InlineData("pässword")]
    public void Read_RejectsPasswordCharactersThatBreakConnectionStrings(string password)
    {
        InvalidOperationException rejected = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.Postgres,
            ("Database:RuntimePrincipals:0:Name", "worker"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:0:Password", password)));
        rejected.Message.ShouldContain("Database:RuntimePrincipals:0:Password");
        rejected.Message.ShouldNotContain(password);
    }

    [Fact]
    public void Read_RejectsDuplicateNamesAndUnknownScopeWithoutEchoingValues()
    {
        InvalidOperationException duplicate = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.Postgres,
            ("Database:RuntimePrincipals:0:Name", "Worker"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:1:Name", "worker"),
            ("Database:RuntimePrincipals:1:Scope", "control-plane")));
        duplicate.Message.ShouldContain(":Name");

        InvalidOperationException scope = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.Postgres,
            ("Database:RuntimePrincipals:0:Name", "worker"),
            ("Database:RuntimePrincipals:0:Scope", "unexpected-scope")));
        scope.Message.ShouldContain(":Scope");
        scope.Message.ShouldNotContain("unexpected-scope");
    }

    [Fact]
    public void Read_ValidatesEntraIdsAndRequiresTheActiveProvidersIdentifier()
    {
        InvalidOperationException postgresId = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.Postgres,
            ("Database:RuntimePrincipals:0:Name", "worker"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:0:EntraClientId", "1f658b22-0c7d-45e7-99a5-36b86cbb2634")));
        postgresId.Message.ShouldContain("EntraObjectId");

        InvalidOperationException invalidId = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.SqlServer,
            ("Database:RuntimePrincipals:0:Name", "worker"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:0:EntraClientId", "private-id-value")));
        invalidId.Message.ShouldContain("EntraClientId");
        invalidId.Message.ShouldNotContain("private-id-value");
    }

    [Fact]
    public void Read_RejectsPasswordAndEntraCredentialsTogether()
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.SqlServer,
            ("Database:RuntimePrincipals:0:Name", "worker"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane"),
            ("Database:RuntimePrincipals:0:Password", "private-password"),
            ("Database:RuntimePrincipals:0:EntraClientId", "1f658b22-0c7d-45e7-99a5-36b86cbb2634")));

        exception.Message.ShouldContain("Password");
        exception.Message.ShouldNotContain("private-password");
    }

    [Fact]
    public void Read_RejectsInvalidNames()
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => Read(
            DatabaseProvider.Postgres,
            ("Database:RuntimePrincipals:0:Name", "worker runtime"),
            ("Database:RuntimePrincipals:0:Scope", "data-plane")));

        exception.Message.ShouldContain(":Name");
        exception.Message.ShouldNotContain("worker runtime");
    }

    private static IReadOnlyList<RuntimePrincipal> Read(
        DatabaseProvider provider,
        params (string Key, string? Value)[] values) =>
        RuntimePrincipalConfiguration.Read(Configuration(values), provider);

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();
}
