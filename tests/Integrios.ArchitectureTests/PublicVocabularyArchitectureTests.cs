using System.Text.RegularExpressions;

namespace Integrios.ArchitectureTests;

/// The repository's own docs are the one place the resource model is described in prose, and prose
/// has no compiler. When Connection was removed, `docs/` was updated and `README.md` was not - the
/// front door of a public repository kept defining a resource the product no longer had. This is
/// the check that would have caught it.
public sealed class PublicVocabularyArchitectureTests
{
    /// Resources that were removed outright. A word here must never reappear as a product noun.
    private static readonly string[] RetiredResourceNouns = ["Connection", "Connections"];

    /// Prose files a reader or an agent is pointed at first. Not `docs/`, which is swept separately
    /// by its own narrative review, and not the private Brain, which is not published.
    private static readonly string[] FrontDoorDocuments = ["README.md", "AGENTS.md", "CONTRIBUTING.md"];

    [Fact]
    public void FrontDoorDocuments_DoNotNameARetiredResource()
    {
        string repositoryRoot = ProjectArchitectureTests.FindRepositoryRoot();
        List<string> offences = [];

        foreach (string document in FrontDoorDocuments)
        {
            string path = Path.Combine(repositoryRoot, document);
            if (!File.Exists(path))
                continue;

            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                // "connection string" and "database connection" are the transport sense of the word
                // and are not the retired resource.
                string line = Regex.Replace(
                    lines[index],
                    @"connection[ -]?(string|pool|factory)|database connection|ConnectionStrings",
                    string.Empty,
                    RegexOptions.IgnoreCase);

                foreach (string noun in RetiredResourceNouns)
                {
                    if (Regex.IsMatch(line, $@"\b{noun}\b", RegexOptions.IgnoreCase))
                        offences.Add($"{document}:{index + 1} names the retired resource '{noun}'");
                }
            }
        }

        offences.ShouldBeEmpty(
            "A document a reader meets first must not describe a resource the product does not have. "
            + string.Join("; ", offences));
    }
}
