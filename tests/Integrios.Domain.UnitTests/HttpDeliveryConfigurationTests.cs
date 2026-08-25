using System.Text.Json;
using Integrios.Domain.ValueObjects;

namespace Integrios.Domain.UnitTests;

public sealed class HttpDeliveryConfigurationTests
{
    [Fact]
    public void Configuration_SerializesTheExactVersionOneShape()
    {
        var configuration = new HttpDeliveryConfiguration
        {
            Version = 1,
            Method = "PATCH",
            Path = "contacts",
            Headers = new Dictionary<string, string> { ["X-Operation"] = "upsert" },
            Body = "json"
        };

        string json = JsonSerializer.Serialize(configuration, StoredJson.Options);

        json.ShouldBe(
            """{"version":1,"method":"PATCH","path":"contacts","headers":{"X-Operation":"upsert"},"body":"json"}""");
    }

    [Theory]
    [InlineData("""{"version":1,"method":"POST","headers":{},"body":"json","unknown":true}""")]
    [InlineData("""{"version":1,"method":"POST","path_expression":{"engine":"jsonata","version":"1","expression":"id"},"headers":{},"body":"json"}""")]
    public void Configuration_RejectsUnknownMembers(string json)
    {
        Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<HttpDeliveryConfiguration>(json, StoredJson.Options));
    }
}
