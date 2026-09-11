using System.Text.Json;

namespace Integrios.Application.Secrets;

/// A credential selection names its secrets by logical reference and never carries a value. Inbound
/// Source verification and outbound Destination authentication both express that as the same
/// {field: reference} document, so they share one check.
///
/// The check is what stands between an Operator pasting a signing key where its name belongs and
/// that key being written verbatim into a control-plane column. Intake and delivery already fail
/// closed on an unresolvable reference, so without this the only symptom is a credential sitting in
/// the database with nothing to say it is there.
public static class SecretReferenceMap
{
    /// The rejection reason, or null when every reference in the document is a reference.
    public static string? Validate(JsonElement secretRefs, string section)
    {
        if (secretRefs.ValueKind != JsonValueKind.Object)
            return $"{section} must be a JSON object.";

        foreach (JsonProperty property in secretRefs.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                return $"Secret reference '{property.Name}' must be a lowercase snake_case string.";

            if (!SecretReferenceName.IsValid(property.Value.GetString()))
            {
                return $"Secret reference '{property.Name}' must be a lowercase logical name of 1 to 63 "
                    + "characters. It names a secret; it is never the secret itself.";
            }
        }

        return null;
    }
}
