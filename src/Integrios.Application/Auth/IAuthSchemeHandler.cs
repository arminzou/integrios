using System.Text.Json;

namespace Integrios.Application.Auth;

public interface IAuthSchemeHandler
{
    string Name { get; }
    IReadOnlyList<string> RequiredConfigFields { get; }
    IReadOnlyList<string> RequiredSecretFields { get; }
    void Apply(HttpRequestMessage request, JsonElement config, IReadOnlyDictionary<string, string> secrets);
}
