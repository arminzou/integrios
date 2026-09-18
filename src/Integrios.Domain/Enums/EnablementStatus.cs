namespace Integrios.Domain.Enums;

/// Whether a Source, Destination, or Subscription takes part in new runtime work. Reversible, and
/// orthogonal to editing: either state can be edited, and either can be deleted. Removal is deletion,
/// never a status.
public enum EnablementStatus
{
    Enabled,
    Disabled,
}
