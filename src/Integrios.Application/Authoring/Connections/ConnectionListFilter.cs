using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Connections;

/// <summary>
/// What a Connections list is scoped to. One record rather than a growing parameter list, and the
/// same record the cursor is scoped by: a cursor is only valid for the filters it was issued under,
/// so anything added here has to reach the scope string in the repository or a stale cursor would
/// silently page through a different set.
/// </summary>
public sealed record ConnectionListFilter(
    OperationalStatus? Status = null,
    string? Environment = null,
    string? ConnectorKey = null,
    string? NameContains = null);

/// <summary>
/// A Connection with the Connector key it was built from, which lives on a different table.
/// </summary>
public sealed record ConnectionListRow(Connection Connection, string ConnectorKey);
