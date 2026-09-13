namespace Integrios.Application.Delivery;

/// <summary>
/// The Operator-only read behind the Admin single-Event view. Registered in the Admin host alone,
/// so the data plane has no way to resolve it and no way to return what it carries.
/// </summary>
public interface IEventDiagnosticsLookup
{
    Task<EventDiagnosticsDto?> GetAsync(Guid tenantId, Guid eventId, CancellationToken cancellationToken);
}
