using System.Net.Http.Json;

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
}
