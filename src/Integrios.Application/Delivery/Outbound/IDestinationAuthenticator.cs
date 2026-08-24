using System.Text.Json;

namespace Integrios.Application.Delivery;

public interface IDestinationAuthenticator
{
    string Name { get; }
    IReadOnlyList<string> RequiredConfigFields { get; }
    IReadOnlyList<string> RequiredSecretFields { get; }
    IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config);
    void Apply(IDictionary<string, string> headers, JsonElement config, IReadOnlyDictionary<string, string> secrets);
}
