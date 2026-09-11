using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Delivery;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Infrastructure.UnitTests;

public sealed class ExampleConnectorManifestTests
{
    private static readonly string[] ExpectedFiles =
        ["dataverse.json", "github.json", "http.json", "slack.json"];

    [Fact]
    public void Examples_AreTheCurrentSetAndParseAsOrdinaryOperatorInput()
    {
        string[] files = Directory.GetFiles(ExamplesDirectory(), "*.json")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        files.ShouldBe(ExpectedFiles);
        foreach (string file in files)
            ParseExample(file).Key.ShouldBe(Path.GetFileNameWithoutExtension(file));
    }

    [Fact]
    public void Examples_HaveNoConcreteSourceOrSubscriptionContract()
    {
        foreach (string file in ExpectedFiles)
        {
            JsonElement document = JsonSerializer.Deserialize<JsonElement>(
                File.ReadAllText(Path.Combine(ExamplesDirectory(), file)));

            document.TryGetProperty("source_contracts", out _).ShouldBeFalse();
            document.TryGetProperty("http_success", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void ProductionProjects_DoNotLoadConnectorExamples()
    {
        string[] productionFiles = Directory.GetFiles(Path.Combine(RepositoryRoot(), "src"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj")
            .ToArray();

        productionFiles.ShouldNotBeEmpty();
        foreach (string file in productionFiles)
            File.ReadAllText(file).ShouldNotContain("examples/connectors", Case.Insensitive);
    }

    private static ConnectorManifest ParseExample(string fileName)
    {
        string path = Path.Combine(ExamplesDirectory(), fileName);
        JsonElement document = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        return ConnectorManifestParser.Parse(
            document,
            new DestinationAuthenticatorRegistry([new ApiKeyHeaderAuthenticator(), new BearerTokenAuthenticator()]));
    }

    private static string ExamplesDirectory() => Path.Combine(RepositoryRoot(), "examples", "connectors");

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
