using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;

namespace Integrios.ArchitectureTests;

public sealed class ProjectArchitectureTests
{
    private static readonly string[] ProductionAssemblyNames =
    [
        "Integrios.Domain",
        "Integrios.Application",
        "Integrios.Infrastructure",
        "Integrios.Admin",
        "Integrios.Ingress",
        "Integrios.Worker",
        "Integrios.MockSink"
    ];

    [Fact]
    public void ProductionAssemblies_RespectDependencyDirectionAndIsolation()
    {
        IReadOnlyDictionary<string, Assembly> assemblies = LoadProductionAssemblies();

        AssertOmitsReferences(
            assemblies["Integrios.Domain"],
            "Integrios.Application",
            "Integrios.Infrastructure",
            "Integrios.Admin",
            "Integrios.Ingress",
            "Integrios.Worker",
            "Integrios.MockSink");
        AssertOmitsReferencePrefixes(
            assemblies["Integrios.Domain"],
            "Microsoft.EntityFrameworkCore",
            "Npgsql");

        AssertOmitsReferences(
            assemblies["Integrios.Application"],
            "Integrios.Infrastructure",
            "Integrios.Admin",
            "Integrios.Ingress",
            "Integrios.Worker",
            "Integrios.MockSink");
        AssertOmitsReferencePrefixes(
            assemblies["Integrios.Application"],
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "Dapper",
            "Jsonata.Net.Native");

        AssertOmitsReferences(
            assemblies["Integrios.Infrastructure"],
            "Integrios.Admin",
            "Integrios.Ingress",
            "Integrios.Worker",
            "Integrios.MockSink");

        AssertOmitsReferences(assemblies["Integrios.Admin"], "Integrios.Ingress", "Integrios.Worker", "Integrios.MockSink");
        AssertOmitsReferences(assemblies["Integrios.Ingress"], "Integrios.Admin", "Integrios.Worker", "Integrios.MockSink");
        AssertOmitsReferences(assemblies["Integrios.Worker"], "Integrios.Admin", "Integrios.Ingress", "Integrios.MockSink");
    }

    [Fact]
    public void ProductionProjects_DeclareOnlyApprovedProjectAndApplicationPackageReferences()
    {
        string repositoryRoot = FindRepositoryRoot();
        IReadOnlyDictionary<string, string[]> allowedProjectReferences = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Integrios.Domain"] = [],
            ["Integrios.Application"] = ["Integrios.Domain"],
            ["Integrios.Infrastructure"] = ["Integrios.Application", "Integrios.Domain"],
            ["Integrios.Admin"] = ["Integrios.Application", "Integrios.Domain", "Integrios.Infrastructure"],
            ["Integrios.Ingress"] = ["Integrios.Application", "Integrios.Domain", "Integrios.Infrastructure"],
            ["Integrios.Worker"] = ["Integrios.Application", "Integrios.Domain", "Integrios.Infrastructure"],
            ["Integrios.MockSink"] = []
        };

        foreach ((string projectName, string[] expectedReferences) in allowedProjectReferences)
        {
            string projectPath = Path.Combine(repositoryRoot, "src", projectName, $"{projectName}.csproj");
            XDocument project = XDocument.Load(projectPath);
            string[] actualReferences = project
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(
                    include!.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)[^1]))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actualReferences);
        }

        XDocument applicationProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "Integrios.Application",
            "Integrios.Application.csproj"));
        string[] declaredDependencies = applicationProject
            .Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "FrameworkReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Cast<string>()
            .ToArray();

        Assert.DoesNotContain(declaredDependencies, dependency =>
            dependency.Equals("Dapper", StringComparison.OrdinalIgnoreCase)
            || dependency.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
            || dependency.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)
            || dependency.StartsWith("Jsonata", StringComparison.OrdinalIgnoreCase)
            || dependency.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));

        AssertApplicationRestoreGraphIsExplicitlyApproved(repositoryRoot);
    }

    [Fact]
    public void SourceTree_HasNoGenericContractBuckets()
    {
        string repositoryRoot = FindRepositoryRoot();
        string srcRoot = Path.Combine(repositoryRoot, "src");
        string[] bannedDirectoryNames = ["Contracts", "Interfaces", "Abstractions"];

        string[] offenders = Directory
            .EnumerateDirectories(srcRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Where(path => bannedDirectoryNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            ".brain/AGENTS.md:28 bans generic Contracts/, Interfaces/, or Abstractions/ buckets "
            + $"anywhere in src/; namespaces stay feature-based, not directory-mirrored. Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ProductionTypes_NeverUseServiceSuffix()
    {
        string[] offenders = ProductionAssemblyNames
            .SelectMany(name => Assembly.Load(name).GetTypes())
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Select(type => type.FullName!)
            .Where(name => name.EndsWith("Service", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Rule 6: a port names the capability, an implementation names its vendor; *Service is "
            + $"banned as the suffix that means nothing. Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void SourceTree_DeclaresNoJsonPropertyNameAttributes()
    {
        string repositoryRoot = FindRepositoryRoot();
        string attributeName = string.Concat("JsonProperty", "Name");

        string[] offenders = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            // Any mention at all, so a fully-qualified or aliased usage cannot slip past.
            .Where(path => File.ReadAllText(path).Contains(attributeName, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "JSON casing is a policy set once per host, never a per-field decision. Types stored as "
            + "JSON carry their own serializer options instead. Found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Infrastructure_ExportsOnlyHostCompositionExtensions()
    {
        string[] exportedTypes = Assembly.Load("Integrios.Infrastructure")
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] approvedTypes =
        [
            "Integrios.Infrastructure.DependencyInjection",
            "Integrios.Infrastructure.SecretResolutionDependencyInjection",
            "Integrios.Infrastructure.Telemetry.TelemetryExtensions"
        ];

        string[] unapproved = exportedTypes.Except(approvedTypes, StringComparer.Ordinal).ToArray();
        string[] missing = approvedTypes.Except(exportedTypes, StringComparer.Ordinal).ToArray();

        Assert.True(
            unapproved.Length == 0 && missing.Length == 0,
            "Integrios.Infrastructure may export only host-composition DI extensions; any other "
            + $"public type leaks an implementation detail across the layer boundary. {DescribeSetDiff(unapproved, missing)}");
    }

    internal static string DescribeSetDiff(IReadOnlyCollection<string> unapproved, IReadOnlyCollection<string> missing)
    {
        var parts = new List<string>();
        if (unapproved.Count > 0)
            parts.Add($"Unapproved: {string.Join(", ", unapproved)}.");
        if (missing.Count > 0)
            parts.Add($"Missing: {string.Join(", ", missing)}.");
        return string.Join(" ", parts);
    }

    [Fact]
    public void DomainTypes_NeverUseReservedCapabilitySuffixes()
    {
        string[] bannedSuffixes = ["Details", "Info", "Data"];
        string[] offenders = Assembly.Load("Integrios.Domain")
            .GetTypes()
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Select(type => type.Name)
            .Where(name => bannedSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Rule 5: capability values carry semantic ubiquitous-language names, never *Details, "
            + $"*Info, or *Data. Found: {string.Join(", ", offenders)}");
    }

    private static IReadOnlyDictionary<string, Assembly> LoadProductionAssemblies() =>
        ProductionAssemblyNames.ToDictionary(
            name => name,
            name => Assembly.Load(new AssemblyName(name)),
            StringComparer.Ordinal);

    private static void AssertOmitsReferences(Assembly assembly, params string[] forbiddenNames)
    {
        HashSet<string> references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string forbiddenName in forbiddenNames)
            Assert.DoesNotContain(forbiddenName, references);
    }

    private static void AssertOmitsReferencePrefixes(Assembly assembly, params string[] forbiddenPrefixes)
    {
        string[] references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();

        foreach (string forbiddenPrefix in forbiddenPrefixes)
        {
            Assert.DoesNotContain(
                references,
                reference => reference.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
        }
    }

    internal static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedPath(string path)
    {
        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains($"{Path.DirectorySeparatorChar}.artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertApplicationRestoreGraphIsExplicitlyApproved(string repositoryRoot)
    {
        string assetsPath = Path.Combine(
            repositoryRoot,
            "src",
            "Integrios.Application",
            "obj",
            "project.assets.json");
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        JsonElement root = assets.RootElement;
        JsonElement framework = root.GetProperty("project").GetProperty("frameworks").GetProperty("net10.0");

        string[] directDependencies = framework.GetProperty("dependencies")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] approvedDirectDependencies =
        [
            "MediatR",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Diagnostics",
            "Microsoft.Extensions.Logging.Abstractions"
        ];
        Assert.Equal(approvedDirectDependencies.Order(StringComparer.Ordinal), directDependencies);

        string[] frameworkReferences = framework.GetProperty("frameworkReferences")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Microsoft.NETCore.App"], frameworkReferences);

        // The transitive graph is checked for what may not appear rather than pinned exactly: the
        // rule is that Application never acquires a web, database, or transform dependency, even
        // second-hand. An exact list would also fail on a patch bump that adds an unrelated
        // transitive package, which tells us nothing about the rule.
        string[] forbiddenPrefixes = ["Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Npgsql", "Dapper", "Jsonata"];
        string[] resolvedPackages = root.GetProperty("libraries")
            .EnumerateObject()
            .Where(property => property.Value.GetProperty("type").GetString() == "package")
            .Select(property => property.Name.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] violations = resolvedPackages
            .Where(package => forbiddenPrefixes.Any(
                prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Integrios.Application must not resolve a web, database, or transform package, directly "
            + $"or transitively. Found: {string.Join(", ", violations)}");
    }
}
