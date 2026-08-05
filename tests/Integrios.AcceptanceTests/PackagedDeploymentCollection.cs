namespace Integrios.AcceptanceTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PackagedDeploymentCollection : ICollectionFixture<PackagedDeploymentFixture>
{
    public const string Name = "Packaged deployment";
}
