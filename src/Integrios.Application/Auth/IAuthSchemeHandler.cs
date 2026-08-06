using System.Text.Json;

namespace Integrios.Application.Auth;

public interface IAuthSchemeHandler
{
    string Name { get; }
    IReadOnlyList<string> RequiredConfigFields { get; }
    IReadOnlyList<string> RequiredSecretFields { get; }
    IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config);
    void Apply(IDictionary<string, string> headers, JsonElement config, IReadOnlyDictionary<string, string> secrets);
}
