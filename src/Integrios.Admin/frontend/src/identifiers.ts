const uuid = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

/// A route path matches any segment, but every route value in this dashboard is handed to the Admin
/// API as an identifier. Both the route table and the shell check a segment before using it, so a
/// malformed one becomes Not found rather than a request the API has to reject. Its own module
/// because the route table imports the shell, so neither can own it.
export function isIdentifier(value: string | undefined): value is string {
  return value !== undefined && uuid.test(value);
}
