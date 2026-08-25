using System.Xml.Linq;
using static Integrios.ArchitectureTests.ProjectArchitectureTests;

namespace Integrios.ArchitectureTests;

public sealed class TestSuiteArchitectureTests
{
    [Fact]
    public void ApplicationUnitTests_DoNotReferenceInfrastructure()
    {
        string repositoryRoot = FindRepositoryRoot();

        string[] references = ProjectReferenceNames(Path.Combine(
            repositoryRoot,
            "tests",
            "Integrios.Application.UnitTests",
            "Integrios.Application.UnitTests.csproj"));

        references.ShouldNotContain("Integrios.Infrastructure");
    }

    [Fact]
    public void ProductionFriendAssemblies_MatchExplicitAllowlists()
    {
        string repositoryRoot = FindRepositoryRoot();
        IReadOnlyDictionary<string, string[]> approved = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Integrios.Domain"] = [],
            ["Integrios.Application"] = ["Integrios.FunctionalTests", "Integrios.Application.UnitTests"],
            ["Integrios.Infrastructure"] =
            [
                "Integrios.FunctionalTests",
                "Integrios.Migrations.Postgres",
                "Integrios.Migrations.SqlServer",
                "Integrios.Infrastructure.UnitTests"
            ],
            ["Integrios.Admin"] = ["Integrios.FunctionalTests", "Integrios.Admin.UnitTests"],
            ["Integrios.Ingestion"] = ["Integrios.Ingestion.UnitTests"],
            ["Integrios.Worker"] = ["Integrios.Worker.UnitTests"],
            ["Integrios.MockSink"] = []
        };

        foreach ((string projectName, string[] expectedFriends) in approved)
        {
            XDocument project = XDocument.Load(Path.Combine(
                repositoryRoot, "src", projectName, $"{projectName}.csproj"));

            string[] actualFriends = project
                .Descendants("InternalsVisibleTo")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray();

            actualFriends.ShouldBe(expectedFriends.Order(StringComparer.Ordinal), Case.Sensitive,
                $"A production assembly may expose internals only to its owning UnitTests project and "
                + "concretely required Functional/Migrations access; never to a consuming host's "
                + $"tests. {projectName} has an unapproved friend.");
        }
    }

    [Fact]
    public void TestsShared_IsNotATestProjectAndStaysFrameworkFree()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "tests",
            "Integrios.Tests.Shared",
            "Integrios.Tests.Shared.csproj"));

        project.Descendants("IsTestProject")
            .Select(element => element.Value)
            .ShouldBe(["false"]);

        string[] packages = project
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        packages.ShouldNotContain(package =>
            package.Equals("Shouldly", StringComparison.OrdinalIgnoreCase)
            || package.Equals("NSubstitute", StringComparison.OrdinalIgnoreCase)
            || package.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AcceptedProjectSet_ContainsNoIntegrationTestsProject()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] projectRoots = [Path.Combine(repositoryRoot, "src"), Path.Combine(repositoryRoot, "tests")];

        string[] offenders = projectRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            .Where(path => Path.GetFileNameWithoutExtension(path).Contains(
                "IntegrationTests",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        (offenders.Length == 0).ShouldBeTrue(
            "The accepted project set has no IntegrationTests project; cross-layer coverage lives in "
            + "FunctionalTests gated by run_kind, never in a dedicated project. "
            + $"Found: {string.Join(", ", offenders)}");
    }

    private static string[] ProjectReferenceNames(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(
                include!.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)[^1]))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
