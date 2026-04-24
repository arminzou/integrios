namespace Integrios.Worker.Infrastructure.Data;

public interface IRoutingRepository
{
    Task<Guid?> FindPipelineIdAsync(Guid tenantId, string eventType, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RouteTarget>> GetActiveRoutesAsync(Guid pipelineId, CancellationToken cancellationToken = default);
}

public record RouteTarget(Guid Id, string Name, string[] MatchEventTypes, Guid DestinationConnectionId, string DestinationUrl);
