import { type ReactNode, useEffect, useRef, useState } from "react";
import { Link, NavLink, Outlet, useLocation, useMatches, useParams } from "react-router";
import { api, loadSession, type OperatorSession, signInHref } from "./api/client";
import type { components } from "./api/schema";
import { isIdentifier } from "./identifiers";
import { sectionHrefs, sectionLabels, sectionOrder, type TenantSection } from "./sections";
import { useResource } from "./ui/useResource";

type Tenant = components["schemas"]["TenantDto"];

type State =
  | { status: "loading" }
  | { status: "anonymous" }
  | { status: "signedIn"; session: OperatorSession }
  | { status: "failed"; message: string };

export function App() {
  const [state, setState] = useState<State>({ status: "loading" });

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

  if (state.status === "loading") return <Shell>Checking your session…</Shell>;
  if (state.status === "failed")
    return (
      <Shell>
        <p role="alert">{state.message}</p>
      </Shell>
    );
  if (state.status === "anonymous")
    return (
      <Shell>
        <p>
          <a href={signInHref()}>Sign in</a> to administer this deployment.
        </p>
      </Shell>
    );

  return <SignedIn session={state.session} />;
}

/// Authentication loading, failure, and anonymous states use the same brand and layout as the
/// signed-in shell without rendering navigation that assumes a session.
function Shell({ children }: { children: ReactNode }) {
  return (
    <div className="shell">
      <header className="topbar">
        <div className="topbar-row">
          <BrandMark />
        </div>
      </header>
      <main id="main">{children}</main>
    </div>
  );
}

function BrandMark() {
  return (
    <span className="brand">
      <span className="brand-mark" aria-hidden="true">
        I
      </span>
      Integrios Operator
    </span>
  );
}

function SignedIn({ session }: { session: OperatorSession }) {
  // The same identifier check the route table applies: without it the shell would read a Tenant the
  // matched route has already refused, turning a malformed URL into an Admin request.
  const params = useParams();
  const tenantId = Object.values(params).every(isIdentifier) ? (params.tenantId ?? null) : null;
  const tenant = useTenant(tenantId);
  const section = useTenantSection();

  return (
    <div className="shell">
      <TopNav session={session} tenantId={tenantId} tenant={tenant} />
      <main id="main">
        {section && tenantId ? (
          <p className="breadcrumb">
            {tenantDisplayName(tenant)} / {sectionLabels[section]}
          </p>
        ) : null}
        <Outlet />
      </main>
    </div>
  );
}

/// The matched route tags itself with the Tenant section it belongs to, so the shell reads that
/// rather than re-deriving it from the path it already matched.
function useTenantSection(): TenantSection | null {
  const matches = useMatches();
  const handle = matches.at(-1)?.handle as { section?: TenantSection } | undefined;
  return handle?.section ?? null;
}

function TopNav({
  session,
  tenantId,
  tenant,
}: {
  session: OperatorSession;
  tenantId: string | null;
  tenant: ReturnType<typeof useTenant>;
}) {
  const headerRef = useRef<HTMLElement>(null);
  const { pathname } = useLocation();

  // The sticky inspector on the Events screen sits below whatever this topbar's real height turns
  // out to be — one row when no Tenant is selected, two when one is — rather than a guessed pixel
  // offset that drifts out of sync the moment this header's own content changes.
  // `tenantId` is a re-run trigger rather than a value this effect reads: it is what re-measures the
  // header when the Tenant row appears or disappears and the bar's height changes.
  // biome-ignore lint/correctness/useExhaustiveDependencies: tenantId is an intentional re-run trigger
  useEffect(() => {
    const element = headerRef.current;
    if (!element) return;
    const updateHeight = () => {
      document.documentElement.style.setProperty("--topbar-height", `${element.offsetHeight}px`);
    };
    updateHeight();
    if (typeof ResizeObserver !== "function") return;
    const observer = new ResizeObserver(updateHeight);
    observer.observe(element);
    return () => observer.disconnect();
  }, [tenantId]);

  return (
    <header className="topbar" ref={headerRef}>
      <nav className="topbar-row" aria-label="Deployment">
        <BrandMark />
        <ul className="nav-list">
          <li>
            {pathname === "/" ? (
              <Link to="/tenants" aria-current="page">
                Tenants
              </Link>
            ) : (
              <NavLink to="/tenants">Tenants</NavLink>
            )}
          </li>
          <li>
            <NavLink to="/connectors">Connectors</NavLink>
          </li>
        </ul>
        <div className="operator-identity">
          <span>
            Signed in as <strong>{session.display_name}</strong>
            {session.email ? ` (${session.email})` : ""}.
          </span>
          {/* A native form submission carries no custom header, so the antiforgery token must
              travel through the server-configured form field rather than the header name used by
              the typed client's own requests. */}
          <form method="post" action="/auth/logout">
            <input type="hidden" name={session.antiforgery_form_field_name} value={session.antiforgery_token} />
            <button type="submit">Sign out</button>
          </form>
        </div>
      </nav>
      {tenantId ? <TenantNav tenantId={tenantId} tenant={tenant} /> : null}
    </header>
  );
}

/// Reads the current Tenant once for the whole shell, so navigation and the breadcrumb name it
/// from the same read instead of each issuing their own GET. `tenantId` is null on deployment-wide
/// routes; the load stays a no-op instant resolution rather than a conditional hook call.
function useTenant(tenantId: string | null) {
  return useResource<Tenant>(
    () =>
      tenantId
        ? api.GET("/admin/tenants/{id}", { params: { path: { id: tenantId } } })
        : Promise.resolve({ data: undefined, error: undefined, response: { status: 200 } }),
    tenantId ?? "",
  );
}

function tenantDisplayName(tenant: ReturnType<typeof useTenant>): string {
  return tenant.data ? tenant.data.name : tenant.problem ? "This Tenant" : "Loading Tenant…";
}

/// Names the current Tenant explicitly in navigation rather than leaving it implied by the route's
/// opaque id.
function TenantNav({ tenantId, tenant }: { tenantId: string; tenant: ReturnType<typeof useTenant> }) {
  return (
    <nav className="topbar-row tenant-row" aria-label="Tenant">
      <span className="tenant-name">{tenantDisplayName(tenant)}</span>
      <ul className="nav-list">
        {sectionOrder.map((section) => (
          <li key={section}>
            <NavLink to={sectionHrefs[section](tenantId)} end={section === "overview"}>
              {sectionLabels[section]}
            </NavLink>
          </li>
        ))}
      </ul>
    </nav>
  );
}
