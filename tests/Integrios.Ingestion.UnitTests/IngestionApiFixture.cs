using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Ingestion.UnitTests;

public sealed class IngestionApiFixture : IDisposable
{
    // Fixed identity used by WebhookEndpointTests to exercise the real
    // ISourceVerificationSecretResolver production path (in-memory configuration source) rather
    // than a stub, per i7a.5's acceptance criteria.
    public const string WebhookTenantSlug = "acme";
    public const string WebhookSecretReference = "webhook-secret";
    public const string WebhookSecretValue = "correct-horse-battery-staple";

    public StubActiveTenantApiKeyLookup TenantApiKeyRepository { get; } = new();
    public StubEventAcceptance EventAcceptance { get; } = new();
    public StubTenantEventLookup EventLookup { get; } = new();
    public StubEventApiSourceResolver EventApiSourceResolver { get; } = new();
    public StubSourceEndpointResolver SourceEndpointResolver { get; } = new();
    public StubBrokerSourceReader BrokerSourceReader { get; } = new();
    public StubTenantApiKeyUseRecorder TenantApiKeyUse { get; } = new();
    public TestHostBuilder Factory { get; }

    public IngestionApiFixture()
    {
        Factory = new TestHostBuilder(IngestionHost.CreateBuilder, IngestionHost.ConfigurePipeline)
            .WithSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test")
            .WithSetting($"SourceSecrets:{WebhookTenantSlug}:{WebhookSecretReference}", WebhookSecretValue)
            .WithServices(services =>
            {
                services.AddSingleton<IActiveTenantApiKeyLookup>(TenantApiKeyRepository);
                services.AddSingleton<IEventAcceptance>(EventAcceptance);
                services.AddSingleton<ITenantEventLookup>(EventLookup);
                services.AddSingleton<IEventApiSourceResolver>(EventApiSourceResolver);
                services.AddSingleton<ISourceEndpointResolver>(SourceEndpointResolver);
                services.AddSingleton<IBrokerSourceReader>(BrokerSourceReader);
                services.AddSingleton<ITenantApiKeyUseRecorder>(TenantApiKeyUse);
            });
    }

    public void Reset()
    {
        TenantApiKeyRepository.Result = null;
        TenantApiKeyUse.Recorded.Clear();
        EventLookup.GetEventResult = null;
        EventApiSourceResolver.Result = new ResolvedEventApiSource
        {
            TopicId = Guid.NewGuid(),
            SourceContractSchema = null,
            SourceMapping = new TransformSpec("jsonata", "1", "{ \"event_type\": \"event\", \"payload\": $ }"),
        };
        SourceEndpointResolver.Result = null;
        EventAcceptance.LastSubmission = null;
        EventAcceptance.ExistingBySourceEventId = null;
    }

    public void Dispose()
    {
        Factory.Dispose();
    }
}

public sealed class StubActiveTenantApiKeyLookup : IActiveTenantApiKeyLookup
{
    public (TenantApiKey TenantApiKey, Tenant Tenant)? Result { get; set; }

    public Task<(TenantApiKey TenantApiKey, Tenant Tenant)?> FindActiveByKeyHashAsync(
        string keyHash,
        CancellationToken cancellationToken = default)
    {
        if (Result is null || Result.Value.TenantApiKey.KeyHash != keyHash)
            return Task.FromResult<(TenantApiKey TenantApiKey, Tenant Tenant)?>(null);
        return Task.FromResult(Result);
    }

}

public sealed class StubTenantApiKeyUseRecorder : ITenantApiKeyUseRecorder
{
    public List<(Guid TenantApiKeyId, DateTimeOffset UsedAt)> Recorded { get; } = [];

    public Task RecordUseAsync(Guid tenantApiKeyId, DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        Recorded.Add((tenantApiKeyId, usedAt));
        return Task.CompletedTask;
    }
}

public sealed class StubEventAcceptance : IEventAcceptance
{
    public EventSubmission? LastSubmission { get; set; }
    public EventAcceptance? ExistingBySourceEventId { get; set; }

    public Task<EventAcceptance?> FindByIdempotencyKeyAsync(
        Guid tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken) => Task.FromResult(ExistingBySourceEventId);

    public Task<EventAcceptance> AcceptAsync(
        EventSubmission submission,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        LastSubmission = submission;
        return Task.FromResult(new EventAcceptance
        {
            EventId = Guid.NewGuid(),
            Status = EventStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
            AlreadyAccepted = false
        });
    }
}

public sealed class StubSourceEndpointResolver : ISourceEndpointResolver
{
    public ResolvedSourceEndpoint? Result { get; set; }

    public Task<ResolvedSourceEndpoint?> ResolveAsync(
        Guid callbackId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result);
}

public sealed class StubTenantEventLookup : ITenantEventLookup
{
    public EventDto? GetEventResult { get; set; }

    public Task<EventDto?> GetByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(GetEventResult);
    }
}

public sealed class StubEventApiSourceResolver : IEventApiSourceResolver
{
    public ResolvedEventApiSource? Result { get; set; }

    public Task<ResolvedEventApiSource?> ResolveAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken)
        => Task.FromResult(Result);
}

// Always empty: these host-composition tests never need a live Azure Service Bus client, and an
// empty reader is exactly the "no compatible Source exists" HTTP-only path the broker receiver
// hosted service is required to handle without touching Azure at all.
public sealed class StubBrokerSourceReader : IBrokerSourceReader
{
    public Task<IReadOnlyList<ResolvedBrokerSource>> ListActiveAzureServiceBusSourcesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ResolvedBrokerSource>>([]);
}
