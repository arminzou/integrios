import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./schema";

/// The signed-in Operator and the antiforgery token the session bootstrap issues. The browser holds
/// no credential of its own: the session cookie travels automatically and is never readable here.
/// Generated from the Admin OpenAPI document rather than hand-maintained, so this type can never
/// drift from the contract `/auth/session` actually serves.
export type OperatorSession = components["schemas"]["OperatorSessionResponse"];

let session: OperatorSession | null = null;

/// Every unsafe request carries the antiforgery token; safe ones never need it.
const browserBoundary: Middleware = {
  async onRequest({ request }) {
    if (session && !["GET", "HEAD", "OPTIONS", "TRACE"].includes(request.method))
      request.headers.set(session.antiforgery_header_name, session.antiforgery_token);
    return request;
  },
  onError() {
    return Response.json(
      { title: "The Admin API could not be reached." },
      { status: 503, headers: { "content-type": "application/problem+json" } },
    );
  },
};

/// The dashboard and the Admin API share one origin, so the base URL is the page's own origin
/// rather than anything configured. Stating it explicitly keeps every request absolute — which
/// `Request` requires outside a browser — without ever addressing a second origin.
export const api = createClient<paths>({
  baseUrl: location.origin,
  credentials: "same-origin",
  // Resolved per request rather than captured once, so the client always uses whatever `fetch` the
  // page currently has.
  fetch: (request) => globalThis.fetch(request),
});
api.use(browserBoundary);

export async function loadSession(): Promise<OperatorSession | null> {
  const response = await fetch("/auth/session", { credentials: "same-origin" });
  if (response.status === 401) {
    session = null;
    return null;
  }
  if (!response.ok) throw new Error(`The session could not be read (${response.status}).`);

  session = (await response.json()) as OperatorSession;
  return session;
}

export function signInHref(): string {
  return `/auth/login?return_to=${encodeURIComponent(location.pathname + location.search)}`;
}
