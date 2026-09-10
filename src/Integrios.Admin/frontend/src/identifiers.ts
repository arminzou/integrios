const uuid = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

/// A route path matches any segment, but every route value in this dashboard is handed to the Admin
/// API as an identifier. Both the route table and the shell check a segment before using it, so a
/// malformed one becomes Not found rather than a request the API has to reject. Its own module
/// because the route table imports the shell, so neither can own it.
export function isIdentifier(value: string | undefined): value is string {
  return value !== undefined && uuid.test(value);
}

/// The identifier shape the Admin API accepts wherever an Operator names something the platform has
/// to match on — a Connector key, a Source contract key, a scheme or mapped field name: lower
/// snake_case beginning with a letter. Read off a display name so an Operator does not have to
/// transliterate one by hand. Anything that cannot begin an identifier is dropped rather than
/// escaped, because this produces a name for the platform rather than a rendering of the original.
export function snakeIdentifier(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^[^a-z]+/, "")
    .replace(/_+$/, "");
}
