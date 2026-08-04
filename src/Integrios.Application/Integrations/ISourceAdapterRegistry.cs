using System.Text.Json;

namespace Integrios.Application.Integrations;

public sealed record SourceAdapterRegistration(
    string Key,
    int ContractVersion,
    bool AuthoringSafe,
    bool AllowsUnverifiedUse,
    IReadOnlyList<string> CompatibleSourceVerificationSchemes,
    Action<JsonElement> ValidateConfig);

public interface ISourceAdapterRegistry
{
    bool TryGet(string key, int contractVersion, out SourceAdapterRegistration registration);
}
