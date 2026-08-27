using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Integrios.Tests.Shared;

public sealed class WireMockSink(HttpClient client)
{
    public async Task AssertHealthyAsync()
    {
        using HttpResponseMessage result = await client.GetAsync("/__admin/health");
        EnsureSuccess(result, "health check");
    }

    public async Task PostAsync(string name, string body, IReadOnlyDictionary<string, string> headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/sink/{name}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach ((string header, string value) in headers)
            request.Headers.Add(header, value);

        using HttpResponseMessage result = await client.SendAsync(request);
        EnsureSuccess(result, "post");
    }

    public async Task ConfigureAsync(
        string name,
        string mode,
        int? delayMs = null,
        int? statusCode = null,
        object? body = null,
        int? retryAfterSeconds = null)
    {
        int status = statusCode ?? (mode == "fail" ? 500 : 200);
        var response = new Dictionary<string, object?> { ["status"] = status };
        if (mode == "slow")
            response["fixedDelayMilliseconds"] = delayMs ?? 2000;
        if (body is not null)
            response["jsonBody"] = body;
        else if (mode != "fail")
            response["jsonBody"] = new { received = true };
        if (retryAfterSeconds is { } retryAfter)
            response["headers"] = new Dictionary<string, string> { ["Retry-After"] = retryAfter.ToString() };

        string id = ControlMappingId(name);
        using (HttpResponseMessage remove = await client.DeleteAsync($"/__admin/mappings/{id}"))
        {
            if (!remove.IsSuccessStatusCode && remove.StatusCode != System.Net.HttpStatusCode.NotFound)
                EnsureSuccess(remove, "replace control");
        }

        using HttpResponseMessage result = await client.PostAsJsonAsync(
            "/__admin/mappings",
            new
            {
                id,
                priority = 1,
                request = new { method = "POST", urlPath = $"/sink/{name}" },
                response,
            });
        EnsureSuccess(result, "configure control");
    }

    public async Task ResetControlAsync(string name)
    {
        using HttpResponseMessage result = await client.DeleteAsync($"/__admin/mappings/{ControlMappingId(name)}");
        if (!result.IsSuccessStatusCode && result.StatusCode != System.Net.HttpStatusCode.NotFound)
            EnsureSuccess(result, "reset control");
    }

    public async Task<int> ReceiptCountAsync(string name) => (await FindReceiptsAsync(name)).RootElement
        .GetProperty("requests")
        .GetArrayLength();

    public async Task AssertReceiptBodyAsync(string name, string expected)
    {
        using JsonDocument receipts = await FindReceiptsAsync(name);
        if (!receipts.RootElement.GetProperty("requests").EnumerateArray().Any(
                receipt => JsonEquivalent(receipt.GetProperty("body").GetString()!, expected)))
        {
            throw new InvalidOperationException("No sink receipt had the expected body.");
        }
    }

    public async Task AssertNoReceiptBodyContainsAsync(string name, string fragment)
    {
        using JsonDocument receipts = await FindReceiptsAsync(name);
        if (receipts.RootElement.GetProperty("requests").EnumerateArray().Any(
                receipt => receipt.GetProperty("body").GetString()!.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A sink receipt contained the forbidden body fragment.");
        }
    }

    public async Task AssertReceiptBodyContainsAsync(string name, string fragment)
    {
        using JsonDocument receipts = await FindReceiptsAsync(name);
        if (!receipts.RootElement.GetProperty("requests").EnumerateArray().Any(
                receipt => receipt.GetProperty("body").GetString()!.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("No sink receipt contained the expected body fragment.");
        }
    }

    public async Task AssertReceiptHeaderAsync(string name, string header, string value)
    {
        using JsonDocument receipts = await FindReceiptsAsync(name);
        bool matched = receipts.RootElement.GetProperty("requests").EnumerateArray().Any(
            receipt => receipt.GetProperty("headers").TryGetProperty(header, out JsonElement values)
                && HeaderMatches(values, value));
        if (!matched)
            throw new InvalidOperationException($"No sink receipt contained the expected {header} header.");
    }

    public async Task AssertReceiptAsync(string name, string body, string header)
    {
        using JsonDocument receipts = await FindReceiptsAsync(name);
        if (!receipts.RootElement.GetProperty("requests").EnumerateArray().Any(
                receipt => receipt.GetProperty("method").GetString() == "POST"
                    && receipt.GetProperty("url").GetString() == $"/sink/{name}"
                    && receipt.GetProperty("headers").TryGetProperty(header, out _)
                    && receipt.GetProperty("body").GetString() == body))
        {
            throw new InvalidOperationException("No sink receipt matched the expected request.");
        }
    }

    public async Task ResetReceiptsAsync(string name)
    {
        using HttpResponseMessage result = await client.DeleteAsync("/__admin/requests");
        EnsureSuccess(result, "reset receipts");
    }

    private async Task<JsonDocument> FindReceiptsAsync(string name)
    {
        using HttpResponseMessage result = await client.PostAsJsonAsync(
            "/__admin/requests/find",
            new { method = "POST", urlPath = $"/sink/{name}" });
        EnsureSuccess(result, "find receipts");
        return await result.Content.ReadFromJsonAsync<JsonDocument>()
            ?? throw new InvalidOperationException("WireMock returned no receipt document.");
    }

    private static string ControlMappingId(string name) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name))[..16]).ToString();

    private static bool JsonEquivalent(string actual, string expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));

    private static bool HeaderMatches(JsonElement values, string expected) => values.ValueKind switch
    {
        JsonValueKind.String => values.GetString() == expected,
        JsonValueKind.Array => values.EnumerateArray().Any(actual => actual.GetString() == expected),
        _ => false,
    };

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WireMock {operation} returned {(int)response.StatusCode}.");
    }
}
