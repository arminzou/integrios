using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Ingestion.UnitTests;

public sealed class IngestionApiFixture : IDisposable
{
    // Fixed identity used by WebhookEndpointTests to exercise the real
    // ISourceVerificationSecretResolver production path (configuration-backed provider) rather
    // than a stub, per i7a.5's acceptance criteria.
    public const string WebhookTenantSlug = "acme";
    public const string WebhookSecretReference = "webhook_secret";
    public const string WebhookSecretValue = "correct-horse-battery-staple";

    public StubActiveTenantApiKeyLookup TenantApiKeyRepository { get; } = new();
    public StubEventAcceptance EventAcceptance { get; } = new();
    public StubTenantEventLookup EventLookup { get; } = new();
    public StubEventApiSourceResolver EventApiSourceResolver { get; } = new();
    public StubSourceEndpointResolver SourceEndpointResolver { get; } = new();
    public StubQueueSourceCatalog QueueSourceCatalog { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    public IngestionApiFixture()
    {
        Factory = new CustomApiFactory(
            TenantApiKeyRepository, EventAcceptance, EventLookup, EventApiSourceResolver, SourceEndpointResolver,
            QueueSourceCatalog);
    }

    public void Reset()
    {
        TenantApiKeyRepository.Result = null;
        EventLookup.GetEventResult = null;
        EventApiSourceResolver.Result = new ResolvedEventApiSource
        {
            TopicId = Guid.NewGuid(),
            SourceContractSchema = null,
            SourceMapping = new TransformSpec("jsonata", "1", "{ \"event_type\": \"event\", \"payload\": $ }"),
        };
        SourceEndpointResolver.Result = null;
        EventAcceptance.LastSubmission = null;
    }

    public void Dispose()
    {
        Factory.Dispose();
    }
}

internal sealed class CustomApiFactory(
    StubActiveTenantApiKeyLookup tenantApiKeyRepository,
    StubEventAcceptance eventAcceptance,
    StubTenantEventLookup eventLookup,
    StubEventApiSourceResolver eventApiSourceResolver,
    StubSourceEndpointResolver sourceEndpointResolver,
    StubQueueSourceCatalog queueSourceCatalog) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Integrios:SourceSecrets:Provider", "configuration");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=test;Username=test;Password=test",
                [$"SourceSecrets:{IngestionApiFixture.WebhookTenantSlug}:{IngestionApiFixture.WebhookSecretReference}"] =
                    IngestionApiFixture.WebhookSecretValue
            }));

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IActiveTenantApiKeyLookup>(tenantApiKeyRepository);
            services.AddSingleton<IEventAcceptance>(eventAcceptance);
            services.AddSingleton<ITenantEventLookup>(eventLookup);
            services.AddSingleton<IEventApiSourceResolver>(eventApiSourceResolver);
            services.AddSingleton<ISourceEndpointResolver>(sourceEndpointResolver);
            services.AddSingleton<IQueueSourceCatalog>(queueSourceCatalog);
        });
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

public sealed class StubEventAcceptance : IEventAcceptance
{
    public EventSubmission? LastSubmission { get; set; }

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
// empty catalog is exactly the "no compatible Source exists" HTTP-only path the queue receiver
// hosted service is required to handle without touching Azure at all.
public sealed class StubQueueSourceCatalog : IQueueSourceCatalog
{
    public Task<IReadOnlyList<ResolvedQueueSource>> ListActiveAzureServiceBusSourcesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ResolvedQueueSource>>([]);
}
