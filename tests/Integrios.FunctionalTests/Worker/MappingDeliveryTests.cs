using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.FunctionalTests.Worker;

public sealed class MappingDeliveryTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public MappingDeliveryTests(WorkerRoutingFixture fixture)
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

        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();
        var deliveredPayload = fixture.DeliveryClient.Calls[0].Payload;

        // The transform was applied: result should not be the raw original payload
        deliveredPayload.ShouldNotBe("{\"test\":true}");
        string.IsNullOrWhiteSpace(deliveredPayload).ShouldBeFalse();

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("succeeded");
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
        fixture.DeliveryClient.Calls.ShouldBeEmpty();

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("pending");
        deliveries[0].AttemptCount.ShouldBe(1);
        deliveries[0].DeliverAfter.ShouldNotBeNull();

        var details = await fixture.GetEventDetailsAsync(eventId);
        details.ShouldNotBeNull();
        var attempt = details.DeliveryAttempts.ShouldHaveSingleItem();
        attempt.Status.ShouldBe("failed");
        attempt.FailurePhase.ShouldBe("transform");
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
        (await fixture.RunFanoutBatchAsync()).ShouldBe(1);

        await fixture.SetSubscriptionTransformByNameAsync("to-ledger", changedTransform);
        await fixture.UpdateLedgerExecutionConfigurationAsync(
            "http://changed-sink/ledger",
            changedAuth,
            "changed_webhook");

        var snapshot = await fixture.GetEventDeliverySnapshotAsync(eventId);
        snapshot.ConnectorKey.ShouldBe("webhook");
        HttpExecutionSnapshot executionSnapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
            snapshot.HttpExecutionSnapshotJson, StoredJson.Options)!;
        executionSnapshot.BaseUri.ShouldBe(WorkerRoutingFixture.LedgerSinkUrl);
        using var expectedAuth = JsonDocument.Parse(originalAuth);
        using var actualAuth = JsonSerializer.SerializeToDocument(
            executionSnapshot.DestinationAuthentication, StoredJson.Options);
        JsonElement.DeepEquals(expectedAuth.RootElement, actualAuth.RootElement).ShouldBeTrue();
        using var expectedTransform = JsonDocument.Parse(originalTransform);
        using var actualMapping = JsonDocument.Parse(snapshot.MappingConfigJson!);
        JsonElement.DeepEquals(expectedTransform.RootElement, actualMapping.RootElement).ShouldBeTrue();

        fixture.DeliveryClient.ShouldSucceed = false;
        (await fixture.RunDeliveryBatchAsync()).ShouldBe(1);
        var firstAttempt = fixture.DeliveryClient.Calls.ShouldHaveSingleItem();
        firstAttempt.Url.ShouldBe(WorkerRoutingFixture.LedgerSinkUrl);
        firstAttempt.Payload.ShouldBe("true");
        firstAttempt.Headers["X-Original-Key"].ShouldBe("original-value-1");

        fixture.SecretResolver.Set("original_token", "original-value-2");
        fixture.DeliveryClient.ShouldSucceed = true;
        await fixture.ForceDeliveryRetryNowAsync(eventId);
        (await fixture.RunDeliveryBatchAsync()).ShouldBe(1);
        var retryAttempt = fixture.DeliveryClient.Calls[1];
        retryAttempt.Url.ShouldBe(WorkerRoutingFixture.LedgerSinkUrl);
        retryAttempt.Headers["X-Original-Key"].ShouldBe("original-value-2");
        retryAttempt.Headers.ContainsKey("X-Changed-Key").ShouldBeFalse();

        var laterEventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        (await fixture.RunFanoutBatchAsync()).ShouldBe(1);
        (await fixture.RunDeliveryBatchAsync()).ShouldBe(1);
        var laterAttempt = fixture.DeliveryClient.Calls[2];
        laterAttempt.Url.ShouldBe("http://changed-sink/ledger");
        laterAttempt.Payload.ShouldBe("false");
        laterAttempt.Headers["X-Changed-Key"].ShouldBe("changed-value");

        var laterSnapshot = await fixture.GetEventDeliverySnapshotAsync(laterEventId);
        HttpExecutionSnapshot laterExecutionSnapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
            laterSnapshot.HttpExecutionSnapshotJson, StoredJson.Options)!;
        laterExecutionSnapshot.BaseUri.ShouldBe("http://changed-sink/ledger");
        laterSnapshot.ConnectorKey.ShouldBe("changed_webhook");
    }

    [Fact]
    public async Task Worker_DestinationConnectionWithoutUrl_FansOutWithNullSnapshotUrl()
    {
        await fixture.ClearLedgerConnectionUrlAsync();
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // Regression: a destination connection without a url must not stall fanout. Before the
        // snapshot column was made nullable, this raised a NOT NULL violation that rolled back the
        // fanout transaction and head-of-line blocked the outbox.
        (await fixture.RunFanoutBatchAsync()).ShouldBe(1);

        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe("pending");

        // A destination without a url normalizes to an empty snapshot url (never a NOT NULL stall),
        // while connector_key stays populated from the inner-joined connector.
        var snapshot = await fixture.GetEventDeliverySnapshotAsync(eventId);
        HttpExecutionSnapshot executionSnapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
            snapshot.HttpExecutionSnapshotJson, StoredJson.Options)!;
        executionSnapshot.BaseUri.ShouldBe(string.Empty);
        snapshot.ConnectorKey.ShouldBe("http");
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
        (await fixture.RunFanoutBatchAsync()).ShouldBe(1);

        var snapshot = await fixture.GetEventDeliverySnapshotAsync(eventId);
        HttpExecutionSnapshot executionSnapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
            snapshot.HttpExecutionSnapshotJson, StoredJson.Options)!;

        executionSnapshot.HttpSuccess.ShouldNotBeNull();
        executionSnapshot.HttpSuccess.Evaluator.ShouldBe("json_boolean");
        executionSnapshot.HttpSuccess.Field.ShouldBe("ok");
        executionSnapshot.HttpSuccess.Expected.ShouldBe(true);
        executionSnapshot.HttpSuccess.DiagnosticField.ShouldBe("error");
        executionSnapshot.HttpSuccess.MaxBodyBytes.ShouldBe(2048);
    }
}
