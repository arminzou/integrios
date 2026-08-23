using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Domain.Connections;

namespace Integrios.Application.FunctionalTests.Worker;

public sealed class TransformDeliveryTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public TransformDeliveryTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Worker_SubscriptionWithTransform_DeliveredPayloadIsTransformed()
    {
        // $.test on {"test":true} extracts the boolean, so the delivered payload differs from the original
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.test"}""";
        await fixture.SetSubscriptionTransformByNameAsync("to-ledger", transformJson);

        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        Assert.Single(fixture.DeliveryClient.Calls);
        var deliveredPayload = fixture.DeliveryClient.Calls[0].Payload;

        // The transform was applied: result should not be the raw original payload
        Assert.NotEqual("{\"test\":true}", deliveredPayload);
        Assert.False(string.IsNullOrWhiteSpace(deliveredPayload));

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("succeeded", deliveries[0].Status);
    }

    [Fact]
    public async Task Worker_SubscriptionWithFailingTransform_DeliverySchedulesRetry()
    {
        // $error() is valid JSONata syntax but throws at evaluation time
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$error(\"forced\")"}""";
        await fixture.SetSubscriptionTransformByNameAsync("to-ledger", transformJson);

        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        // Transform failure is treated as delivery failure: no HTTP call made
        Assert.Empty(fixture.DeliveryClient.Calls);

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("pending", deliveries[0].Status);
        Assert.Equal(1, deliveries[0].AttemptCount);
        Assert.NotNull(deliveries[0].DeliverAfter);

        var details = await fixture.GetEventDetailsAsync(eventId);
        Assert.NotNull(details);
        var attempt = Assert.Single(details.DeliveryAttempts);
        Assert.Equal("failed", attempt.Status);
        Assert.Equal("transform", attempt.FailurePhase);
    }

    [Fact]
    public async Task Worker_ExistingDelivery_UsesFanoutSnapshotAfterConfigurationChanges()
    {
        var originalTransform = """{"engine":"jsonata","version":"1","expression":"$.test"}""";
        var changedTransform = """{"engine":"jsonata","version":"1","expression":"$not($.test)"}""";
        var originalAuth = """{"scheme":"api_key_header","config":{"header_name":"X-Original-Key"},"secret_refs":{"api_key":"original_token"}}""";
        var changedAuth = """{"scheme":"api_key_header","config":{"header_name":"X-Changed-Key"},"secret_refs":{"api_key":"changed_token"}}""";

        fixture.SecretResolver.Set("original_token", "original-value-1");
        fixture.SecretResolver.Set("changed_token", "changed-value");
        await fixture.UpdateLedgerExecutionConfigurationAsync(
            WorkerRoutingFixture.LedgerSinkUrl,
            originalAuth,
            "webhook");
        await fixture.SetSubscriptionTransformByNameAsync("to-ledger", originalTransform);
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        Assert.Equal(1, await fixture.RunFanoutBatchAsync());

        await fixture.SetSubscriptionTransformByNameAsync("to-ledger", changedTransform);
        await fixture.UpdateLedgerExecutionConfigurationAsync(
            "http://changed-sink/ledger",
            changedAuth,
            "changed_webhook");

        var snapshot = await fixture.GetSubscriptionDeliverySnapshotAsync(eventId);
        Assert.Equal("webhook", snapshot.ConnectorKey);
        HttpExecutionSnapshot executionSnapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
            snapshot.HttpExecutionSnapshotJson, ConnectionSchemeSelection.StoredJson)!;
        Assert.Equal(WorkerRoutingFixture.LedgerSinkUrl, executionSnapshot.BaseUri);
        using var expectedAuth = JsonDocument.Parse(originalAuth);
        using var actualAuth = JsonSerializer.SerializeToDocument(
            executionSnapshot.DestinationAuthentication, ConnectionSchemeSelection.StoredJson);
        Assert.True(JsonElement.DeepEquals(expectedAuth.RootElement, actualAuth.RootElement));
        using var expectedTransform = JsonDocument.Parse(originalTransform);
        using var actualTransform = JsonDocument.Parse(snapshot.TransformConfigJson!);
        Assert.True(JsonElement.DeepEquals(expectedTransform.RootElement, actualTransform.RootElement));

        fixture.DeliveryClient.ShouldSucceed = false;
        Assert.Equal(1, await fixture.RunDeliveryBatchAsync());
        var firstAttempt = Assert.Single(fixture.DeliveryClient.Calls);
        Assert.Equal(WorkerRoutingFixture.LedgerSinkUrl, firstAttempt.Url);
        Assert.Equal("true", firstAttempt.Payload);
        Assert.Equal("original-value-1", firstAttempt.Headers["X-Original-Key"]);

        fixture.SecretResolver.Set("original_token", "original-value-2");
        fixture.DeliveryClient.ShouldSucceed = true;
        await fixture.ForceDeliveryRetryNowAsync(eventId);
        Assert.Equal(1, await fixture.RunDeliveryBatchAsync());
        var retryAttempt = fixture.DeliveryClient.Calls[1];
        Assert.Equal(WorkerRoutingFixture.LedgerSinkUrl, retryAttempt.Url);
        Assert.Equal("original-value-2", retryAttempt.Headers["X-Original-Key"]);
        Assert.False(retryAttempt.Headers.ContainsKey("X-Changed-Key"));

        var laterEventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        Assert.Equal(1, await fixture.RunFanoutBatchAsync());
        Assert.Equal(1, await fixture.RunDeliveryBatchAsync());
        var laterAttempt = fixture.DeliveryClient.Calls[2];
        Assert.Equal("http://changed-sink/ledger", laterAttempt.Url);
        Assert.Equal("false", laterAttempt.Payload);
        Assert.Equal("changed-value", laterAttempt.Headers["X-Changed-Key"]);

        var laterSnapshot = await fixture.GetSubscriptionDeliverySnapshotAsync(laterEventId);
        HttpExecutionSnapshot laterExecutionSnapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
            laterSnapshot.HttpExecutionSnapshotJson, ConnectionSchemeSelection.StoredJson)!;
        Assert.Equal("http://changed-sink/ledger", laterExecutionSnapshot.BaseUri);
        Assert.Equal("changed_webhook", laterSnapshot.ConnectorKey);
    }

    [Fact]
    public async Task Worker_DestinationConnectionWithoutUrl_FansOutWithNullSnapshotUrl()
    {
        await fixture.ClearLedgerConnectionUrlAsync();
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // Regression: a destination connection without a url must not stall fanout. Before the
        // snapshot column was made nullable, this raised a NOT NULL violation that rolled back the
        // fanout transaction and head-of-line blocked the outbox.
        Assert.Equal(1, await fixture.RunFanoutBatchAsync());

        var delivery = Assert.Single(await fixture.GetSubscriptionDeliveriesAsync(eventId));
        Assert.Equal("pending", delivery.Status);

        // A destination without a url normalizes to an empty snapshot url (never a NOT NULL stall),
        // while connector_key stays populated from the inner-joined connector.
        var snapshot = await fixture.GetSubscriptionDeliverySnapshotAsync(eventId);
        HttpExecutionSnapshot executionSnapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
            snapshot.HttpExecutionSnapshotJson, ConnectionSchemeSelection.StoredJson)!;
        Assert.Equal(string.Empty, executionSnapshot.BaseUri);
        Assert.Equal("http", snapshot.ConnectorKey);
    }

    [Fact]
    public async Task Worker_ConnectorWithHttpSuccessRule_FansOutWithSnapshotCarryingIt()
    {
        await fixture.UpdateLedgerExecutionConfigurationAsync(
            WorkerRoutingFixture.LedgerSinkUrl,
            null,
            "outcome_contract_test",
            httpSuccessJson: """
                {"evaluator":"json_boolean","field":"ok","expected":true,"diagnostic_field":"error","max_body_bytes":2048}
                """);

        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        Assert.Equal(1, await fixture.RunFanoutBatchAsync());

        var snapshot = await fixture.GetSubscriptionDeliverySnapshotAsync(eventId);
        HttpExecutionSnapshot executionSnapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
            snapshot.HttpExecutionSnapshotJson, ConnectionSchemeSelection.StoredJson)!;

        Assert.NotNull(executionSnapshot.HttpSuccess);
        Assert.Equal("json_boolean", executionSnapshot.HttpSuccess.Evaluator);
        Assert.Equal("ok", executionSnapshot.HttpSuccess.Field);
        Assert.True(executionSnapshot.HttpSuccess.Expected);
        Assert.Equal("error", executionSnapshot.HttpSuccess.DiagnosticField);
        Assert.Equal(2048, executionSnapshot.HttpSuccess.MaxBodyBytes);
    }
}
