using System.Net.Http.Json;
using System.Text.Json;

namespace Integrios.FunctionalTests.Admin;

public abstract class AdminApiTestBase
{
    protected static HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalOperatorAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }

    protected static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Get, url));
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{url} -> {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
