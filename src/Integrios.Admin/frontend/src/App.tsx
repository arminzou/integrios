import { useQuery } from "@tanstack/react-query";
import {
  Activity,
  ArrowDownToLine,
  ArrowRight,
  Building2,
  GitBranch,
  Hash,
  Info,
  KeyRound,
  LayoutDashboard,
  type LucideIcon,
  Package,
  RotateCcw,
  TriangleAlert,
  Waypoints,
} from "lucide-react";
import { Fragment, useEffect } from "react";
import { Link, NavLink, Outlet, useLocation, useMatches, useParams } from "react-router";
import { Button } from "@/components/ui/button";
import { api, loadSession, type OperatorSession, signInHref } from "./api/client";
import { asProblem, call } from "./api/query";
import { isIdentifier } from "./identifiers";
import { sectionGroups, sectionHrefs, sectionLabels, type TenantSection } from "./sections";
import { ReadError } from "./ui/controls";

/// The session bootstrap is a server read like every other read in the dashboard, so it is read the
/// same way. The four-state union and the cancellation flag it needed were an inline reimplementation
/// of what the query client already does; `isPending`, `error`, and `data` are the same three states
/// without the bookkeeping. A signed-out deployment answers 401, which `loadSession` reports as a
/// null session rather than as a failure — being signed out is an answer, not an error.
export function App() {
  const session = useQuery({ queryKey: ["session"], queryFn: loadSession });

  if (session.isPending) return <SessionGate title="Integrios Operator" copy="Checking your session…" />;
  if (session.isError)
    return (
      <SessionGate
        title="Integrios Operator"
        copy="This is not a sign-in problem. The deployment answered, but not with a session."
        alert={session.error instanceof Error ? session.error.message : String(session.error)}
        detail="Signing in again will not help until the Admin API answers."
        action="Retry"
        onRetry={() => session.refetch()}
      />
    );
  if (!session.data) return <SignedOutGate />;

  return <SignedIn session={session.data} />;
}

function SignedOutGate() {
  const query = new URLSearchParams(location.search);
  if (query.get("error") === "access_denied")
    return (
      <SessionGate
        title="Sign-in did not complete"
        copy="Nothing was signed in. Trying again is safe."
        alert="Your identity provider refused this sign-in."
        detail="If it keeps refusing, ask whoever administers it whether you are assigned to this application."
        action="Try again"
      />
    );

  if (query.get("signed_out") === "1")
    return (
      <SessionGate
        title="You are signed out"
        copy="This Integrios session has ended. Your identity provider session was left as it was."
        action="Sign in again"
      />
    );

  const deepLink = location.pathname !== "/" || location.search !== "";
  return (
    <SessionGate
      title="Integrios Operator"
      copy="Sign in to administer this deployment."
      detail={deepLink ? "You will be returned to the page you were on." : undefined}
      action="Sign in"
    />
  );
}

function SessionGate({
  title,
  copy,
  alert,
  detail,
  action,
  onRetry,
}: {
  title: string;
  copy: string;
  alert?: string;
  detail?: string;
  action?: string;
  onRetry?: () => void;
}) {
  const Icon = alert ? TriangleAlert : Info;
  const ActionIcon = onRetry || alert ? RotateCcw : ArrowRight;

  return (
    <div className="grid min-h-screen bg-canvas">
      <SkipLink />
      <main id="main" tabIndex={-1} className="grid place-items-center px-5 py-10">
        <div className="flex w-full max-w-[400px] flex-col gap-6">
          <BrandMark />
          <section className="rounded-lg border bg-surface p-6 sm:p-8" aria-labelledby="session-title">
            <div className="flex flex-col">
              <h1 id="session-title" className="min-h-[72px] text-2xl">
                {title}
              </h1>
              <p className="m-0 text-sm text-ink-secondary">{copy}</p>
            </div>
            {alert || detail ? (
              <div
                className={`mt-5 flex gap-2.5 rounded-md p-4 text-sm ${
                  alert ? "bg-danger-surface text-danger-ink" : "bg-surface-quiet"
                }`}
                role={alert ? "alert" : undefined}
              >
                <Icon className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                <div>
                  {alert ? <strong className="block font-semibold">{alert}</strong> : null}
                  {detail ? (
                    <p className={`m-0 ${alert ? "text-danger-ink" : "text-ink-secondary"}`}>{detail}</p>
                  ) : null}
                </div>
              </div>
            ) : null}
            {action ? (
              onRetry ? (
                <Button className="mt-3 w-full" variant="outline" onClick={onRetry}>
                  {action}
                  <ActionIcon aria-hidden="true" />
                </Button>
              ) : (
                <Button asChild className="mt-3 w-full">
                  <a href={signInHref()}>
                    {action}
                    <ActionIcon aria-hidden="true" />
                  </a>
                </Button>
              )
            ) : null}
          </section>
        </div>
      </main>
    </div>
  );
}

/// The signed-in shell, written as utilities on the markup it belongs to. A column beside the
/// document above the `shell` breakpoint, and the wrapping band below it that the previous top
/// navigation was verified with at 320 — so the narrow layout is the base and the column is the
/// variant, rather than a media query undoing a desktop default.
const shell = "grid min-h-screen content-start grid-cols-1 items-start bg-canvas shell:grid-cols-[16rem_minmax(0,1fr)]";

/// No page-level measure: the only thing one would bound here is the ledger, and a ledger wants
/// width. What genuinely needs a measure states its own, where the reason for it is visible.
///
/// `relative` and the screen minimum are what the loading cover resolves against: it fills this
/// element and stops at its edges, so the rail beside it stays visible and operable while a read is
/// outstanding, and it covers a whole column rather than however tall the page happened to be.
///
/// The minimum is confined to the two-column layout. Below that breakpoint the rail is a band above
/// the document rather than a column beside it, so a full-viewport minimum here would put every
/// page over the viewport by the height of that band and scroll a screen that has nothing to scroll.
const document_ = "relative w-full min-w-0 p-4 shell:min-h-screen shell:p-6";

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

/// A sub-group label inside the Tenant list (Author / Observe): the same treatment as a group label,
/// plus air above it so the runs read as separate. The unlabelled API-keys list carries the same air
/// directly, since it has no label to hang it on.
const navSubLabel = `${navLabel} shell:mt-3`;

const navList = "m-0 flex list-none flex-row flex-wrap items-center gap-0.5 p-0 shell:flex-col shell:items-stretch";

/// States its own box, larger than the 24x24 floor the base layer gives a standalone link.
const navLink =
  "flex items-center gap-2 rounded-md px-2.5 py-1.5 no-underline hover:bg-hover-surface " +
  "focus-visible:bg-hover-surface aria-[current=page]:bg-selected-surface " +
  "aria-[current=page]:font-semibold aria-[current=page]:text-selected-ink shell:w-full";

/// An icon is a landmark for a destination an Operator returns to daily. It never carries meaning
/// the label does not already carry, so it is hidden from assistive technology and no label is
/// dropped in favour of one. Destinations and Connectors take deliberately unlike shapes: they are
/// the two nouns that are already confused, and near-identical glyphs would agree with the
/// confusion rather than help.
const navIcon = "size-4 shrink-0 text-ink-secondary group-aria-[current=page]:text-selected-ink";

const sectionIcons: Record<TenantSection, LucideIcon> = {
  overview: LayoutDashboard,
  events: Activity,
  destinations: Waypoints,
  sources: ArrowDownToLine,
  topics: Hash,
  subscriptions: GitBranch,
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
  const tenantProblem = asProblem(tenant.error) ?? { status: 500, errors: {} };
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
        {!tenantId || tenant.data ? (
          <>
            <Breadcrumb section={section} title={title} tenantId={tenantId} tenant={tenant} />
            <Outlet />
          </>
        ) : tenant.isPending ? (
          <p role="status">Loading Tenant…</p>
        ) : (
          <>
            <h1>{tenantProblem.status === 404 ? "Tenant not found" : "Tenant unavailable"}</h1>
            <ReadError problem={tenantProblem} what="This Tenant" back={{ to: "/tenants", label: "Go to Tenants" }} />
            {tenantProblem.status !== 404 ? (
              <Button type="button" variant="outline" onClick={() => tenant.refetch()}>
                Retry
              </Button>
            ) : null}
          </>
        )}
      </main>
    </div>
  );
}

/// Where the page sits, starting at the deployment rather than at the open Tenant: a Tenant is a
/// scope inside the deployment, and the trail that omits it cannot say so. A deployment-wide screen
/// carries a single segment rather than nothing, because the line above the title is where a page
/// states its place, and leaving it blank on two screens makes them read as placeless.
function Breadcrumb({
  section,
  title,
  tenantId,
  tenant,
}: {
  section?: TenantSection;
  title?: string;
  tenantId: string | null;
  tenant: ReturnType<typeof useTenant>;
}) {
  const trail =
    section && tenantId ? ["Tenants", tenantDisplayName(tenant), sectionLabels[section]] : title ? [title] : [];
  if (trail.length === 0) return null;

  return <p className="m-0 mb-1.5 text-xs text-ink-secondary">{trail.join(" / ")}</p>;
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

      {tenantId && tenant.data ? <TenantNav tenantId={tenantId} tenant={tenant} /> : null}

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
      {sectionGroups.map((group, index) => (
        <Fragment key={group.label ?? group.sections[0]}>
          {group.label ? <p className={navSubLabel}>{group.label}</p> : null}
          <ul className={navList + (index > 0 && !group.label ? " shell:mt-3" : "")}>
            {group.sections.map((section) => (
              <li key={section}>
                <NavLink
                  to={sectionHrefs[section](tenantId)}
                  end={section === "overview"}
                  className={`group ${navLink}`}
                >
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
        </Fragment>
      ))}
    </nav>
  );
}
