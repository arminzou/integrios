import { useQuery } from "@tanstack/react-query";
import { type ReactNode, useEffect } from "react";
import { Link, NavLink, Outlet, useLocation, useMatches, useParams } from "react-router";
import { Button } from "@/components/ui/button";
import { api, loadSession, type OperatorSession, signInHref } from "./api/client";
import { call } from "./api/query";
import { isIdentifier } from "./identifiers";
import { sectionHrefs, sectionLabels, sectionOrder, type TenantSection } from "./sections";

/// The session bootstrap is a server read like every other read in the dashboard, so it is read the
/// same way. The four-state union and the cancellation flag it needed were an inline reimplementation
/// of what the query client already does; `isPending`, `error`, and `data` are the same three states
/// without the bookkeeping. A signed-out deployment answers 401, which `loadSession` reports as a
/// null session rather than as a failure — being signed out is an answer, not an error.
export function App() {
  const session = useQuery({ queryKey: ["session"], queryFn: loadSession });

  if (session.isPending) return <Shell>Checking your session…</Shell>;
  if (session.isError)
    return (
      <Shell>
        <p role="alert">{session.error instanceof Error ? session.error.message : String(session.error)}</p>
      </Shell>
    );
  if (!session.data)
    return (
      <Shell>
        <p>
          <a href={signInHref()}>Sign in</a> to administer this deployment.
        </p>
      </Shell>
    );

  return <SignedIn session={session.data} />;
}

/// Authentication loading, failure, and anonymous states use the same brand and layout as the
/// signed-in shell without rendering navigation that assumes a session.
function Shell({ children }: { children: ReactNode }) {
  return (
    <div className="shell">
      <SkipLink />
      <header className="signed-out-bar">
        <BrandMark />
      </header>
      <main id="main" tabIndex={-1}>
        {children}
      </main>
    </div>
  );
}

/// The first thing a keyboard reaches. The rail carries every destination in the dashboard, so
/// without this every screen costs that many tab stops before its own content. `#main` takes
/// `tabIndex={-1}` so following the link moves focus rather than only scrolling.
function SkipLink() {
  return (
    <a
      href="#main"
      className="sr-only rounded-md border bg-surface px-3 py-2 text-sm font-medium focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-10"
    >
      Skip to content
    </a>
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
  const { section, title } = useRouteHandle();

  // Names where the Operator is, so history and a restored window say more than the product name.
  // The Tenant joins the title only once its name has actually been read: a loading placeholder
  // here would be what a bookmark captured.
  const tenantName = tenant.data?.name;
  useEffect(() => {
    const parts = [section ? sectionLabels[section] : title, tenantName, "Integrios Operator"];
    document.title = parts.filter(Boolean).join(" · ");
  }, [section, title, tenantName]);

  return (
    <div className="shell">
      <SkipLink />
      <Rail session={session} tenantId={tenantId} tenant={tenant} />
      <main id="main" tabIndex={-1}>
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

/// The matched route tags itself with the Tenant section it belongs to and the name its tab should
/// carry, so the shell reads both rather than re-deriving either from the path it already matched.
type RouteHandle = { section?: TenantSection; title?: string };

function useRouteHandle(): RouteHandle {
  const matches = useMatches();
  return (matches.at(-1)?.handle as RouteHandle | undefined) ?? {};
}

/// The signed-in navigation. A rail rather than a bar because the two scopes this dashboard has —
/// deployment-wide and Tenant-scoped — are a grouping a horizontal row cannot express: in a rail
/// each group carries its own label, and when no Tenant is open the Tenant group is simply absent
/// rather than a row left blank. It also spends the axis the product can spare; the ledger is the
/// widest thing here and wants the horizontal space navigation would otherwise take.
///
/// Below 860 CSS pixels it lays out as a horizontal band instead, which is the same wrapping
/// behaviour the previous top navigation was verified with at 320.
function Rail({
  session,
  tenantId,
  tenant,
}: {
  session: OperatorSession;
  tenantId: string | null;
  tenant: ReturnType<typeof useTenant>;
}) {
  const { pathname } = useLocation();

  return (
    <div className="rail">
      <BrandMark />

      {tenantId ? <TenantNav tenantId={tenantId} tenant={tenant} /> : null}

      <nav className="nav-group" aria-label="Deployment">
        <p className="nav-label">Deployment</p>
        <ul className="nav-list">
          <li>
            {/* `/` is an alias for the Tenants list rather than a redirect, so it keeps any query
                and fragment a copied link carried. NavLink cannot mark that one case itself. */}
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
      </nav>

      <div className="rail-foot">
        <span className="operator-identity">
          Signed in as <strong>{session.display_name}</strong>
          {session.email ? ` (${session.email})` : ""}.
        </span>
        {/* A native form submission carries no custom header, so the antiforgery token must
            travel through the server-configured form field rather than the header name used by
            the typed client's own requests. */}
        <form method="post" action="/auth/logout">
          <input type="hidden" name={session.antiforgery_form_field_name} value={session.antiforgery_token} />
          <Button type="submit" variant="outline" size="sm">
            Sign out
          </Button>
        </form>
      </div>
    </div>
  );
}

/// Reads the current Tenant once for the whole shell, so navigation and the breadcrumb name it
/// from the same read instead of each issuing their own GET. `tenantId` is null on deployment-wide
/// routes, and the query is simply disabled there rather than the hook being called conditionally.
function useTenant(tenantId: string | null) {
  return useQuery({
    queryKey: ["tenant", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{id}", { params: { path: { id: tenantId as string } } })),
    // A deployment-wide route names no Tenant, so there is nothing to read.
    enabled: tenantId !== null,
  });
}

function tenantDisplayName(tenant: ReturnType<typeof useTenant>): string {
  return tenant.data ? tenant.data.name : tenant.isError ? "This Tenant" : "Loading Tenant…";
}

/// Names the current Tenant explicitly in navigation rather than leaving it implied by the route's
/// opaque id. The name doubles as the way back to the Tenants list: the route stays authoritative
/// for which Tenant is open, so this is an affordance for changing it, never a second source of it.
function TenantNav({ tenantId, tenant }: { tenantId: string; tenant: ReturnType<typeof useTenant> }) {
  return (
    <nav className="nav-group" aria-label="Tenant">
      <p className="nav-label">Tenant</p>
      <Link className="tenant-switch" to="/tenants">
        <span className="tenant-name">{tenantDisplayName(tenant)}</span>
        {tenant.data?.environment ? <span className="tenant-meta">{tenant.data.environment}</span> : null}
      </Link>
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
