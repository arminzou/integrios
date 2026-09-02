import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./schema";

/// The signed-in Operator and the antiforgery token the session bootstrap issues. The browser holds
/// no credential of its own: the session cookie travels automatically and is never readable here.
/// Generated from the Admin OpenAPI document rather than hand-maintained, so this type can never
/// drift from the contract `/auth/session` actually serves.
export type OperatorSession = components["schemas"]["OperatorSessionResponse"];

let session: OperatorSession | null = null;

/// Every unsafe request carries the antiforgery token; safe ones never need it.
const antiforgery: Middleware = {
  async onRequest({ request }) {
    if (session && !["GET", "HEAD", "OPTIONS", "TRACE"].includes(request.method))
      request.headers.set(session.antiforgery_header_name, session.antiforgery_token);
    return request;
  },
};

export const api = createClient<paths>({ credentials: "same-origin" });
api.use(antiforgery);

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
