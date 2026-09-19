using System.Data.Common;
using System.Text.Json;
using Dapper;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class EventDiagnosticsActionsTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private const string TraceUrlTemplateKey = "Integrios:Admin:TraceUrlTemplate";
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Diagnostics_ReportWhetherAnUnroutedEventCanStillBeRouted()
    {
        var (routedEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (sourceId, topicId) = await OriginOfAsync(routedEventId);
        // The seeded Source declares recovery.test; the match ignores case, as acceptance does.
        Guid actionable = await InsertEventAsync(sourceId, topicId, "Recovery.Test", "unrouted");
        Guid undeclared = await InsertEventAsync(sourceId, topicId, "recovery.retired", "unrouted");

        (await DiagnosticsAsync(client, actionable)).GetProperty("unrouted_actionable").GetBoolean().ShouldBeTrue();
        (await DiagnosticsAsync(client, undeclared)).GetProperty("unrouted_actionable").GetBoolean().ShouldBeFalse();
        (await DiagnosticsAsync(client, routedEventId)).GetProperty("unrouted_actionable").GetBoolean().ShouldBeFalse();

        // Deleting the only declaring Source leaves the Event readable but historical-only.
        await ExecuteAsync($"UPDATE sources SET deleted_at = {fixture.Now} WHERE id = @Id", new { Id = sourceId });
        JsonElement historical = await DiagnosticsAsync(client, actionable);
        historical.GetProperty("unrouted_actionable").GetBoolean().ShouldBeFalse();
        historical.GetProperty("status").GetString().ShouldBe("unrouted");
    }

    [Fact]
    public async Task Diagnostics_OfferNoTraceLinkWithoutATemplate()
    {
        var (eventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        await fixture.AddOutboxTraceparentAsync(eventId, "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

        JsonElement detail = await DiagnosticsAsync(client, eventId);

        detail.GetProperty("trace_id").GetString().ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        detail.GetProperty("trace_url").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Diagnostics_ResolveTheConfiguredTemplateWithoutExposingIt()
    {
        var (tracedEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (untracedEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        await fixture.AddOutboxTraceparentAsync(tracedEventId, "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        using WebApplicationFactory<Program> configured = fixture.WebFactory.WithWebHostBuilder(builder =>
            builder.UseSetting(TraceUrlTemplateKey, "https://tracing.example.test/trace/{trace_id}?view=waterfall"));
        using HttpClient tracing = configured.CreateClient();

        JsonElement traced = await DiagnosticsAsync(tracing, tracedEventId);
        traced.GetProperty("trace_url").GetString()
            .ShouldBe("https://tracing.example.test/trace/4bf92f3577b34da6a3ce929d0e0e4736?view=waterfall");
        traced.GetRawText().ShouldNotContain("{trace_id}");
        (await DiagnosticsAsync(tracing, untracedEventId)).GetProperty("trace_url").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Startup_FailsForAMalformedTemplate()
    {
        using WebApplicationFactory<Program> misconfigured = fixture.WebFactory.WithWebHostBuilder(builder =>
            builder.UseSetting(TraceUrlTemplateKey, "https://tracing.example.test/trace/"));

        Exception failure = Should.Throw<Exception>(() => misconfigured.CreateClient());
        Exception? cause = failure;
        while (cause is not null && !cause.Message.Contains(TraceUrlTemplateKey, StringComparison.Ordinal))
            cause = cause.InnerException;
        cause.ShouldNotBeNull(failure.ToString());
    }

    private async Task<JsonElement> DiagnosticsAsync(HttpClient http, Guid eventId)
    {
        string url = $"/admin/tenants/{fixture.TenantId}/events/{eventId}/deliveries";
        using HttpResponseMessage response = await http.SendAsync(AdminRequest(HttpMethod.Get, url));
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{url} -> {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task<(Guid SourceId, Guid TopicId)> OriginOfAsync(Guid eventId)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<(Guid, Guid)>(
            "SELECT source_id, topic_id FROM events WHERE id = @Id", new { Id = eventId });
    }

    private async Task<Guid> InsertEventAsync(Guid sourceId, Guid topicId, string eventType, string status)
    {
        Guid id = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO events (id, tenant_id, source_id, topic_id, event_type, payload, status)
            VALUES (@Id, @TenantId, @SourceId, @TopicId, @EventType, {{fixture.Json("@Payload")}}, @Status);
            """,
            new { Id = id, fixture.TenantId, SourceId = sourceId, TopicId = topicId, EventType = eventType, Payload = "{}", Status = status });
        return id;
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
