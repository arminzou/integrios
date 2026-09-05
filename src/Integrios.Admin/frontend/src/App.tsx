import { type ReactNode, useEffect, useRef, useState } from "react";
import { api, loadSession, type OperatorSession, signInHref } from "./api/client";
import type { components } from "./api/schema";
import { type Route, useRoute } from "./routes";
import { ConnectionScreen, ConnectionsScreen } from "./screens/Connections";
import { ConnectorScreen, ConnectorsScreen } from "./screens/Connectors";
import { EventsScreen } from "./screens/Events";
import { SourceScreen, SourcesScreen } from "./screens/Sources";
import { SubscriptionScreen } from "./screens/Subscriptions";
import { TenantApiKeysScreen } from "./screens/TenantApiKeys";
import { TenantScreen, TenantsScreen } from "./screens/Tenants";
import { TopicScreen, TopicsScreen } from "./screens/Topics";
import { Link } from "./ui/controls";
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
  const route = useRoute();
  const tenantId = "tenantId" in route ? route.tenantId : null;
  const tenant = useTenant(tenantId);
  const section = tenantSectionOf(route);

  return (
    <div className="shell">
      <TopNav session={session} route={route} tenantId={tenantId} tenant={tenant} />
      <main id="main">
        {section ? (
          <p className="breadcrumb">
            {tenantDisplayName(tenant)} / {sectionLabels[section]}
          </p>
        ) : null}
        <Screen route={route} />
      </main>
    </div>
  );
}

function isConnectorsSection(route: Route): boolean {
  return route.name === "connectors" || route.name === "connector";
}

function TopNav({
  session,
  route,
  tenantId,
  tenant,
}: {
  session: OperatorSession;
  route: Route;
  tenantId: string | null;
  tenant: ReturnType<typeof useTenant>;
}) {
  const connectorsActive = isConnectorsSection(route);
  const headerRef = useRef<HTMLElement>(null);

  // The sticky inspector on the Events screen sits below whatever this topbar's real height turns
  // out to be — one row when no Tenant is selected, two when one is — rather than a guessed pixel
  // offset that drifts out of sync the moment this header's own content changes.
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
            <Link to="/tenants" current={!connectorsActive && route.name !== "unknown"}>
              Tenants
            </Link>
          </li>
          <li>
            <Link to="/connectors" current={connectorsActive}>
              Connectors
            </Link>
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
      {tenantId ? <TenantNav tenantId={tenantId} route={route} tenant={tenant} /> : null}
    </header>
  );
}

type TenantSection = "overview" | "events" | "connections" | "sources" | "topics" | "apiKeys";

const sectionLabels: Record<TenantSection, string> = {
  overview: "Overview",
  events: "Events",
  connections: "Connections",
  sources: "Sources",
  topics: "Topics",
  apiKeys: "API keys",
};

function tenantSectionOf(route: Route): TenantSection | null {
  switch (route.name) {
    case "tenant":
      return "overview";
    case "events":
    case "event":
      return "events";
    case "connections":
    case "connection":
      return "connections";
    case "sources":
    case "source":
      return "sources";
    case "topics":
    case "topic":
    case "subscription":
      return "topics";
    case "tenantApiKeys":
      return "apiKeys";
    default:
      return null;
  }
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
function TenantNav({
  tenantId,
  route,
  tenant,
}: {
  tenantId: string;
  route: Route;
  tenant: ReturnType<typeof useTenant>;
}) {
  const section = tenantSectionOf(route);

  const items: { section: TenantSection; href: string }[] = [
    { section: "overview", href: `/tenants/${tenantId}` },
    { section: "events", href: `/tenants/${tenantId}/events` },
    { section: "connections", href: `/tenants/${tenantId}/connections` },
    { section: "sources", href: `/tenants/${tenantId}/sources` },
    { section: "topics", href: `/tenants/${tenantId}/topics` },
    { section: "apiKeys", href: `/tenants/${tenantId}/tenant-api-keys` },
  ];

  return (
    <nav className="topbar-row tenant-row" aria-label="Tenant">
      <span className="tenant-name">{tenantDisplayName(tenant)}</span>
      <ul className="nav-list">
        {items.map((item) => (
          <li key={item.section}>
            <Link to={item.href} current={item.section === section}>
              {sectionLabels[item.section]}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}

function Screen({ route }: { route: Route }) {
  switch (route.name) {
    case "tenants":
      return <TenantsScreen />;
    case "tenant":
      return <TenantScreen tenantId={route.tenantId} />;
    case "connections":
      return <ConnectionsScreen tenantId={route.tenantId} />;
    case "connection":
      return <ConnectionScreen tenantId={route.tenantId} connectionId={route.connectionId} />;
    case "events":
      return <EventsScreen tenantId={route.tenantId} />;
    case "event":
      return <EventsScreen tenantId={route.tenantId} selectedEventId={route.eventId} />;
    case "tenantApiKeys":
      return <TenantApiKeysScreen tenantId={route.tenantId} />;
    case "sources":
      return <SourcesScreen tenantId={route.tenantId} />;
    case "source":
      return <SourceScreen tenantId={route.tenantId} sourceId={route.sourceId} />;
    case "topics":
      return <TopicsScreen tenantId={route.tenantId} />;
    case "topic":
      return <TopicScreen tenantId={route.tenantId} topicId={route.topicId} />;
    case "subscription":
      return (
        <SubscriptionScreen tenantId={route.tenantId} topicId={route.topicId} subscriptionId={route.subscriptionId} />
      );
    case "connectors":
      return <ConnectorsScreen />;
    case "connector":
      return <ConnectorScreen connectorId={route.connectorId} />;
    case "unknown":
      return (
        <>
          <h1>Not found</h1>
          <p role="alert">
            No Operator screen owns <code>{route.path}</code>.
          </p>
          <p>
            <Link to="/tenants">Go to Tenants</Link>
          </p>
        </>
      );
  }
}
