using Integrios.Application.Abstractions;
using Integrios.Application.Events;
using Integrios.Domain.Common;
using Integrios.Domain.Events;
using Integrios.Domain.Tenants;
using Integrios.Domain.Topics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Ingress.Tests;

public sealed class ApiTestAppFixture : IDisposable
{
    public StubApiKeyRepository ApiKeyRepository { get; } = new();
    public StubEventRepository EventRepository { get; } = new();
    public StubTopicRepository TopicRepository { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    public ApiTestAppFixture()
    {
        Factory = new CustomApiFactory(ApiKeyRepository, EventRepository, TopicRepository);
    }

    public void Reset()
    {
        ApiKeyRepository.Result = null;
        EventRepository.GetEventResult = null;
        EventRepository.ReplayResult = false;
        TopicRepository.ResolvedTopicId = Guid.NewGuid();
    }

    public void Dispose()
    {
        Factory.Dispose();
    }
}

internal sealed class CustomApiFactory(
    StubApiKeyRepository apiKeyRepository,
    StubEventRepository eventRepository,
    StubTopicRepository topicRepository) : WebApplicationFactory<Program>
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
            services.AddSingleton<IApiKeyRepository>(apiKeyRepository);
            services.AddSingleton<IEventRepository>(eventRepository);
            services.AddSingleton<ITopicRepository>(topicRepository);
        });
    }
}

public sealed class StubApiKeyRepository : IApiKeyRepository
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

    public Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<(IReadOnlyList<ApiKey> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

public sealed class StubEventRepository : IEventRepository
{
    public GetEventResponse? GetEventResult { get; set; }
    public bool ReplayResult { get; set; } = false;

    public Task<IngestEventResponse> IngestAsync(
        Guid tenantId,
        IngestEventRequest request,
        Guid? topicId,
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

    public Task<bool> ReplayEventAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ReplayResult);
    }
}

public sealed class StubTopicRepository : ITopicRepository
{
    public Guid? ResolvedTopicId { get; set; } = Guid.NewGuid();

    public Task<Guid?> FindByNameAsync(Guid tenantId, string name, CancellationToken ct = default)
        => Task.FromResult(ResolvedTopicId);

    public Task<Topic> CreateAsync(Guid tenantId, string name, string? description, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Topic?> UpdateAsync(Guid tenantId, Guid id, string name, string? description, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> SetSourceConnectionsAsync(Guid tenantId, Guid id, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct = default)
        => throw new NotImplementedException();
}
