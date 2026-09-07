import { useQuery } from "@tanstack/react-query";
import {
  Activity,
  ArrowDownToLine,
  Building2,
  Hash,
  KeyRound,
  LayoutDashboard,
  type LucideIcon,
  Package,
  Waypoints,
} from "lucide-react";
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
    <div className={shell}>
      <SkipLink />
      <header className="border-b bg-surface px-4 py-3">
        <BrandMark />
      </header>
      <main id="main" tabIndex={-1} className={document_}>
        {children}
      </main>
    </div>
  );
}

/// The signed-in shell, written as utilities on the markup it belongs to. A column beside the
/// document above the `shell` breakpoint, and the wrapping band below it that the previous top
/// navigation was verified with at 320 — so the narrow layout is the base and the column is the
/// variant, rather than a media query undoing a desktop default.
const shell = "grid min-h-screen grid-cols-1 items-start bg-canvas shell:grid-cols-[232px_minmax(0,1fr)]";

/// No page-level measure: the only thing one would bound here is the ledger, and a ledger wants
/// width. What genuinely needs a measure states its own, where the reason for it is visible.
const document_ = "w-full min-w-0 p-4 shell:p-6";

const rail =
  "flex flex-row flex-wrap items-center gap-x-3 gap-y-2 border-b bg-surface px-3 py-4 text-sm " +
  "shell:sticky shell:top-0 shell:h-screen shell:flex-col shell:flex-nowrap shell:items-stretch " +
  "shell:gap-5 shell:overflow-y-auto shell:border-r shell:border-b-0";

const navGroup = "flex flex-row flex-wrap items-center gap-0.5 shell:flex-col shell:items-stretch";

/// Hidden below the breakpoint rather than removed: the band has no room for a group label, but the
/// `nav` landmark still carries the same word, so the grouping survives for a screen reader.
const navLabel =
  "sr-only shell:not-sr-only shell:m-0 shell:mb-1 shell:px-2.5 shell:text-[0.6875rem] " +
  "shell:font-semibold shell:tracking-[0.07em] shell:text-ink-secondary shell:uppercase";

const navList = "m-0 flex list-none flex-row flex-wrap items-center gap-0.5 p-0 shell:flex-col shell:items-stretch";

/// States its own box, larger than the 24x24 floor the base layer gives a standalone link.
const navLink =
  "flex items-center gap-2 rounded-md px-2.5 py-1.5 no-underline hover:bg-hover-surface " +
  "focus-visible:bg-hover-surface aria-[current=page]:bg-selected-surface " +
  "aria-[current=page]:font-semibold aria-[current=page]:text-selected-ink shell:w-full";

/// An icon is a landmark for a destination an Operator returns to daily. It never carries meaning
/// the label does not already carry, so it is hidden from assistive technology and no label is
/// dropped in favour of one. Connections and Connectors take deliberately unlike shapes: they are
/// the two nouns that are already confused, and near-identical glyphs would agree with the
/// confusion rather than help.
const navIcon = "size-4 shrink-0 text-ink-secondary group-aria-[current=page]:text-selected-ink";

const sectionIcons: Record<TenantSection, LucideIcon> = {
  overview: LayoutDashboard,
  events: Activity,
  connections: Waypoints,
  sources: ArrowDownToLine,
  topics: Hash,
  apiKeys: KeyRound,
};

function NavIcon({ icon: Icon }: { icon: LucideIcon }) {
  return <Icon className={navIcon} aria-hidden="true" focusable="false" />;
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
    <span className="inline-flex items-center gap-[9px] px-2 font-semibold tracking-[-0.01em]">
      {/* A generic mark, not a reproduction of any product's brand — a solid square so the wordmark
          reads as an app identity at a glance. */}
      <span className="size-[18px] flex-none rounded bg-ink" aria-hidden="true" />
      Integrios
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
    <div className={shell}>
      <SkipLink />
      <Rail session={session} tenantId={tenantId} tenant={tenant} />
      <main id="main" tabIndex={-1} className={document_}>
        {section && tenantId ? (
          <p className="m-0 mb-1.5 text-xs text-ink-secondary">
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
    <div className={rail} data-shell="rail">
      <BrandMark />

      {tenantId ? <TenantNav tenantId={tenantId} tenant={tenant} /> : null}

      <nav className={navGroup} aria-label="Deployment">
        <p className={navLabel}>Deployment</p>
        <ul className={navList}>
          <li>
            {/* `/` is an alias for the Tenants list rather than a redirect, so it keeps any query
                and fragment a copied link carried. NavLink cannot mark that one case itself. */}
            {pathname === "/" ? (
              <Link to="/tenants" aria-current="page" className={`group ${navLink}`}>
                <NavIcon icon={Building2} />
                Tenants
              </Link>
            ) : (
              // `end` so Tenants is not also marked current on every Tenant-scoped screen beneath
              // it. It is the ancestor scope of the open Tenant, and `aria-current="page"` names
              // the page being viewed, not the branch it sits on.
              <NavLink to="/tenants" end className={`group ${navLink}`}>
                <NavIcon icon={Building2} />
                Tenants
              </NavLink>
            )}
          </li>
          <li>
            <NavLink to="/connectors" className={`group ${navLink}`}>
              <NavIcon icon={Package} />
              Connectors
            </NavLink>
          </li>
        </ul>
      </nav>

      <div className="ml-auto flex items-center justify-between gap-2 shell:mt-auto shell:ml-0 shell:border-t shell:pt-3">
        {/* The identity is a label, not a sentence: the rail has room for a name and the role it is
            acting in, and the email only repeats what the name already said. */}
        <span className="min-w-0" title={session.email ?? undefined}>
          <strong className="block overflow-hidden font-medium text-ellipsis">{session.display_name}</strong>
          <span className="block text-xs text-ink-secondary">Operator</span>
        </span>
        {/* A native form submission carries no custom header, so the antiforgery token must
            travel through the server-configured form field rather than the header name used by
            the typed client's own requests. */}
        <form method="post" action="/auth/logout">
          <input type="hidden" name={session.antiforgery_form_field_name} value={session.antiforgery_token} />
          <Button type="submit" variant="outline" size="sm" className="shrink-0">
            Sign out
          </Button>
        </form>
      </div>
    </div>
  );
}

/// Outstanding, not recent. This read was originally the Event activity summary, which is windowed
/// to the last hour — so a Tenant with nine Deliveries that exhausted their retries this morning
/// showed no badge at all, which is the one thing a badge for unattended work must never do. The
/// Tenant overview counts the same Deliveries without a window, and is the read the Overview screen
/// already makes, so the shell shares it rather than issuing its own.
function useDeadLetteredCount(tenantId: string): number {
  const overview = useQuery({
    queryKey: ["tenant-overview", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{id}/overview", { params: { path: { id: tenantId } } })),
  });
  return Number(overview.data?.dead_lettered_deliveries ?? 0);
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
  // A count rides the destination it belongs to: work that has stopped retrying is the one thing an
  // Operator has to see from wherever they are standing, not only from the ledger that lists it. It
  // is the same Tenant-scoped summary the Overview and the Events screen read, so the shell costs no
  // request they were not already making.
  const deadLettered = useDeadLetteredCount(tenantId);

  return (
    <nav className={navGroup} aria-label="Tenant">
      <p className={navLabel}>Tenant</p>
      <Link
        className="flex flex-row items-baseline gap-1.5 rounded-md border px-2.5 py-2 no-underline hover:bg-hover-surface focus-visible:bg-hover-surface shell:mb-1.5 shell:flex-col shell:items-stretch shell:gap-px"
        to="/tenants"
      >
        <span className="font-semibold">{tenantDisplayName(tenant)}</span>
        {tenant.data?.environment ? (
          <span className="text-xs text-ink-secondary">{tenant.data.environment}</span>
        ) : null}
      </Link>
      <ul className={navList}>
        {sectionOrder.map((section) => (
          <li key={section}>
            <NavLink to={sectionHrefs[section](tenantId)} end={section === "overview"} className={`group ${navLink}`}>
              <NavIcon icon={sectionIcons[section]} />
              {sectionLabels[section]}
              {/* A count rides the destination it belongs to, so work that has stopped retrying is
                  visible from anywhere in the Tenant rather than only from the ledger listing it. */}
              {section === "events" && deadLettered > 0 ? (
                <span className="ml-auto rounded-full bg-danger-surface px-1.5 py-px text-xs text-danger-ink tabular-nums">
                  {deadLettered}
                </span>
              ) : null}
            </NavLink>
          </li>
        ))}
      </ul>
    </nav>
  );
}
