/// Connector manifests, Connection configuration, Source configuration, and Subscription match
/// rules and mapping are domain JSON documents whose shape the Connector contract owns, not the
/// dashboard. They are authored as JSON text and only checked for well-formedness here; the server
/// remains the authority on whether the document is valid.
export function parseJson(text: string): { value: unknown; error?: undefined } | { value?: undefined; error: string } {
  if (text.trim() === "") return { error: "Enter a JSON document." };
  try {
    return { value: JSON.parse(text) as unknown };
  } catch (failure) {
    return { error: failure instanceof Error ? failure.message : "The value is not valid JSON." };
  }
}

export function formatJson(value: unknown): string {
  return value === undefined || value === null ? "" : JSON.stringify(value, null, 2);
}
