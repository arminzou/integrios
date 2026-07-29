using Integrios.Application.ApiKeys;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Domain.Common;
using Integrios.Domain.Events;
using Integrios.Domain.Tenants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Ingress.Tests;

public sealed class ApiTestAppFixture : IDisposable
{
    public StubActiveApiKeyLookup ApiKeyRepository { get; } = new();
    public StubEventRepository EventRepository { get; } = new();
    public StubDeadLetterReplay DeliveryQueue { get; } = new();
    public StubIntakeTopicResolver TopicRepository { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    public ApiTestAppFixture()
    {
        Factory = new CustomApiFactory(ApiKeyRepository, EventRepository, TopicRepository, DeliveryQueue);
    }

    public void Reset()
    {
        ApiKeyRepository.Result = null;
        EventRepository.GetEventResult = null;
        DeliveryQueue.ReplayResult = false;
        TopicRepository.ResolvedTopicId = Guid.NewGuid();
    }

    public void Dispose()
    {
        Factory.Dispose();
    }
}

internal sealed class CustomApiFactory(
    StubActiveApiKeyLookup apiKeyRepository,
    StubEventRepository eventRepository,
    StubIntakeTopicResolver topicRepository,
    StubDeadLetterReplay deliveryQueue) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=test;Username=test;Password=test"
            }));

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IActiveApiKeyLookup>(apiKeyRepository);
            services.AddSingleton<IEventRepository>(eventRepository);
            services.AddSingleton<IIntakeTopicResolver>(topicRepository);
            services.AddSingleton<IDeadLetterReplay>(deliveryQueue);
        });
    }
}

public sealed class StubActiveApiKeyLookup : IActiveApiKeyLookup
{
    public (ApiKey ApiKey, Tenant Tenant)? Result { get; set; }

    public Task<(ApiKey ApiKey, Tenant Tenant)?> FindActiveByKeyHashAsync(
        string keyHash,
        CancellationToken cancellationToken = default)
    {
        if (Result is null || Result.Value.ApiKey.KeyHash != keyHash)
            return Task.FromResult<(ApiKey ApiKey, Tenant Tenant)?>(null);
        return Task.FromResult(Result);
    }

}

public sealed class StubEventRepository : IEventRepository
{
    public GetEventResponse? GetEventResult { get; set; }

    public Task<IngestEventResponse> IngestAsync(
        Guid tenantId,
        IngestEventRequest request,
        Guid topicId,
        string? traceparent = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new IngestEventResponse
        {
            EventId = Guid.NewGuid(),
            Status = EventStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
            IsDuplicate = false
        });
    }

    public Task<GetEventResponse?> GetEventByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetEventResult);
    }

}

public sealed class StubDeadLetterReplay : IDeadLetterReplay
{
    public bool ReplayResult { get; set; }

    public Task<bool> ReplayDeadLetteredAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ReplayResult);
}

public sealed class StubIntakeTopicResolver : IIntakeTopicResolver
{
    public Guid? ResolvedTopicId { get; set; } = Guid.NewGuid();

    public Task<Guid?> FindActiveSourceTopicAsync(Guid tenantId, string name, Guid sourceConnectionId, CancellationToken ct = default)
        => Task.FromResult(ResolvedTopicId);
}
