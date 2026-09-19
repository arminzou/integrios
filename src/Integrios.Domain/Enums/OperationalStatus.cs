namespace Integrios.Domain.Enums;

/// Whether a Tenant, Source, Destination, or Subscription takes part in new runtime work. Reversible,
/// and orthogonal to editing: either state can be edited, and either can be deleted. Removal is
/// deletion, never a status. Operator intent only — observed runtime health uses separate vocabulary.
public enum OperationalStatus
{
    Active = 0,
    Inactive = 1,
}
