using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Integrios.QualificationTests;

[Collection(PackagedDeploymentCollection.Name)]
[Trait("Category", "Qualification")]
public sealed class PackagedDeploymentSmokeTests(PackagedDeploymentFixture fixture)
{
    [Fact]
    public async Task PackagedDeployment_StartsAndExposesDeterministicEvidence()
    {
        await AssertHealthyAsync(fixture.AdminClient);
        await AssertHealthyAsync(fixture.IngressClient);
        await AssertHealthyAsync(fixture.MockSinkClient);

        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM integrations WHERE key = 'webhook' AND status = 'active'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM admin_keys WHERE revoked_at IS NULL"));

        const string sinkName = "qualification-harness";
        const string headerValue = "expected-value";
        const string body = "{\"event\":\"packaged-deployment\"}";

        using HttpResponseMessage resetBefore = await fixture.MockSinkClient.DeleteAsync($"/receipts/{sinkName}");
        Assert.Equal(HttpStatusCode.OK, resetBefore.StatusCode);

        using var delivery = new HttpRequestMessage(HttpMethod.Post, $"/sink/{sinkName}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        delivery.Headers.Add("X-Qualification", headerValue);
        using HttpResponseMessage delivered = await fixture.MockSinkClient.SendAsync(delivery);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);

        using JsonDocument receiptQuery = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>(
            $"/receipts/{sinkName}") ?? throw new InvalidOperationException("MockSink returned no receipt document.");
        JsonElement root = receiptQuery.RootElement;
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        JsonElement receipt = root.GetProperty("receipts")[0];
        Assert.Equal("POST", receipt.GetProperty("method").GetString());
        Assert.Equal($"/sink/{sinkName}", receipt.GetProperty("path").GetString());
        Assert.Equal(body, receipt.GetProperty("body").GetString());
        Assert.Contains(
            "X-Qualification",
            receipt.GetProperty("headerNames").EnumerateArray().Select(value => value.GetString()));

        using HttpResponseMessage headerAssertion = await fixture.MockSinkClient.PostAsJsonAsync(
            $"/receipts/{sinkName}/assert-headers",
            new { headers = new Dictionary<string, string> { ["X-Qualification"] = headerValue } });
        string assertionEvidence = await headerAssertion.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, headerAssertion.StatusCode);
        Assert.Contains("\"matched\":true", assertionEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(headerValue, assertionEvidence, StringComparison.Ordinal);

        using HttpResponseMessage resetAfter = await fixture.MockSinkClient.DeleteAsync($"/receipts/{sinkName}");
        Assert.Equal(HttpStatusCode.OK, resetAfter.StatusCode);
        using JsonDocument emptyQuery = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>(
            $"/receipts/{sinkName}") ?? throw new InvalidOperationException("MockSink returned no reset receipt document.");
        Assert.Equal(0, emptyQuery.RootElement.GetProperty("count").GetInt32());
    }

    private static async Task AssertHealthyAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
