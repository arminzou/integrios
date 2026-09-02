import { useEffect, useState } from "react";
import { api, loadSession, signInHref, type OperatorSession } from "./api/client";

type State =
  | { status: "loading" }
  | { status: "anonymous" }
  | { status: "signedIn"; session: OperatorSession }
  | { status: "failed"; message: string };

/// The shell proves the browser contract end to end: one origin, a cookie session resolved through
/// the bootstrap, and a typed client generated from the Admin document. Capability screens arrive
/// as their own slices rather than being stubbed here.
export function App() {
  const [state, setState] = useState<State>({ status: "loading" });
  const [tenants, setTenants] = useState<string[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    loadSession()
      .then((session) => {
        if (cancelled) return;
        setState(session ? { status: "signedIn", session } : { status: "anonymous" });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        setState({ status: "failed", message: error instanceof Error ? error.message : String(error) });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // One real typed call against the generated contract. It is what makes the frontend check fail
  // when the Admin API changes shape, rather than drifting until a screen breaks in a browser.
  useEffect(() => {
    if (state.status !== "signedIn") return;
    let cancelled = false;
    api.GET("/admin/tenants", { params: { query: { limit: 20 } } }).then(({ data }) => {
      if (cancelled) return;
      setTenants((data?.items ?? []).map((tenant) => tenant.name));
    });
    return () => {
      cancelled = true;
    };
  }, [state.status]);

  return (
    <main>
      <h1>Integrios Operator</h1>
      {state.status === "loading" && <p>Checking your session…</p>}
      {state.status === "anonymous" && (
        <p>
          <a href={signInHref()}>Sign in</a> to administer this deployment.
        </p>
      )}
      {state.status === "signedIn" && (
        <>
          <p>
            Signed in as <strong>{state.session.display_name}</strong>
            {state.session.email ? ` (${state.session.email})` : ""}.
          </p>
          <h2>Tenants</h2>
          {tenants === null ? (
            <p>Loading tenants…</p>
          ) : tenants.length === 0 ? (
            <p>No Tenants are configured yet.</p>
          ) : (
            <ul>
              {tenants.map((name) => (
                <li key={name}>{name}</li>
              ))}
            </ul>
          )}
          {/* A native form submission carries no custom header, so the antiforgery token must
              travel through the server-configured form field rather than the header name used by
              the typed client's own requests. */}
          <form method="post" action="/auth/logout">
            <input
              type="hidden"
              name={state.session.antiforgery_form_field_name}
              value={state.session.antiforgery_token}
            />
            <button type="submit">Sign out</button>
          </form>
        </>
      )}
      {state.status === "failed" && <p role="alert">{state.message}</p>}
    </main>
  );
}
