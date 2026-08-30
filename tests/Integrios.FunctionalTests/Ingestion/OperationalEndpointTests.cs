extern alias IngestionHost;

using System.Net;

namespace Integrios.FunctionalTests.Ingestion;

public sealed class OperationalEndpointTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [Fact]
    public async Task LivenessAndReadiness_WithAvailableDatabase_AreHealthy()
    {
        using HttpClient client = fixture.WebFactory.CreateClient();

        using HttpResponseMessage liveness = await client.GetAsync("/health");
        using HttpResponseMessage readiness = await client.GetAsync("/ready");

        liveness.StatusCode.ShouldBe(HttpStatusCode.OK);
        readiness.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
