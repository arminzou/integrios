/// Connector manifests, Destination configuration, Source configuration, and Subscription match
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

function normalizedJson(value: unknown): unknown {
  return Array.isArray(value)
    ? value.map(normalizedJson)
    : value !== null && typeof value === "object"
      ? Object.fromEntries(
          Object.entries(value)
            .sort(([left], [right]) => left.localeCompare(right))
            .map(([key, item]) => [key, normalizedJson(item)]),
        )
      : value;
}

export function sameJson(left: unknown, right: unknown): boolean {
  return JSON.stringify(normalizedJson(left)) === JSON.stringify(normalizedJson(right));
}

export type JsonObject = Record<string, unknown>;

/// Whether a value is a JSON object at all, for a caller that must tell "absent or wrong shape"
/// from "an object with nothing in it" — which `object` deliberately cannot.
export function isObject(value: unknown): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/// Reading one of those documents back. A value that is not an object reads as an empty one rather
/// than throwing, because the document's shape belongs to the Connector contract and a screen
/// showing a fact it cannot find must still render.
export function object(value: unknown): JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as JsonObject) : {};
}

export function text(value: unknown, key: string): string | null {
  const found = object(value)[key];
  return typeof found === "string" && found.trim() ? found : null;
}
