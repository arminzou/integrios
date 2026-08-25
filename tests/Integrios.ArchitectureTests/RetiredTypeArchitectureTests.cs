using System.Reflection;
using Integrios.Application;

namespace Integrios.ArchitectureTests;

// A retired type does not announce itself. TopicSource and SourceEndpoint survived the rewrite that
// replaced them: unreferenced by any behaviour, still mapped by EF, still creating their tables, and
// still carrying a catch block for a foreign key the schema no longer had. Every architecture test
// passed the whole time, because none of them could see a type that simply lingers.
public sealed class RetiredTypeArchitectureTests
{
    // Names deleted by a completed rewrite. Adding one here is how a rewrite stays finished.
    private static readonly string[] RetiredTypeNames =
    [
        "TopicSource",
        "TopicSourceStatus",
        "SourceEndpoint",
        "Integration",
        "IntegrationManifest",
        "MessageQueueSource",
        "SourceAdapter",
        "QueueBinding",
        "SubscriptionDelivery",
    ];

    [Fact]
    public void RetiredTypes_StayDeleted()
    {
        Assembly[] assemblies =
        [
            typeof(Integrios.Domain.Entities.Source).Assembly,
            ApplicationArchitectureTests.ApplicationAssembly,
            Assembly.Load("Integrios.Infrastructure"),
        ];

        string[] resurrected = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => RetiredTypeNames.Contains(type.Name, StringComparer.Ordinal))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            resurrected.Length == 0,
            "These types were retired by a completed rewrite and must not exist: "
            + string.Join(", ", resurrected)
            + ". If one is genuinely needed again, remove it from RetiredTypeNames deliberately.");
    }
}
