import { useEffect, useState, type ReactNode } from "react";
import { api, loadSession, signInHref, type OperatorSession } from "./api/client";
import { useRoute, type Route } from "./routes";
import type { components } from "./api/schema";
import { Link } from "./ui/controls";
import { useResource } from "./ui/useResource";
import { ConnectorScreen, ConnectorsScreen } from "./screens/Connectors";
import { ConnectionScreen, ConnectionsScreen } from "./screens/Connections";
import { EventsScreen } from "./screens/Events";
import { SourceScreen, SourcesScreen } from "./screens/Sources";
import { SubscriptionScreen } from "./screens/Subscriptions";
import { TenantApiKeysScreen } from "./screens/TenantApiKeys";
import { TenantScreen, TenantsScreen } from "./screens/Tenants";
import { TopicScreen, TopicsScreen } from "./screens/Topics";

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
          <span className="brand">Integrios Operator</span>
        </div>
      </header>
      <main id="main">{children}</main>
    </div>
  );
}

function SignedIn({ session }: { session: OperatorSession }) {
  const route = useRoute();
  const tenantId = "tenantId" in route ? route.tenantId : null;

  return (
    <div className="shell">
      <TopNav session={session} route={route} tenantId={tenantId} />
      <main id="main">
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
}: {
  session: OperatorSession;
  route: Route;
  tenantId: string | null;
}) {
  const connectorsActive = isConnectorsSection(route);

  return (
    <header className="topbar">
      <nav className="topbar-row" aria-label="Deployment">
        <span className="brand">Integrios Operator</span>
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
      {tenantId ? <TenantNav tenantId={tenantId} route={route} /> : null}
    </header>
  );
}

type TenantSection = "overview" | "events" | "connections" | "sources" | "topics" | "apiKeys";

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

/// Names the current Tenant explicitly in navigation rather than leaving it implied by the route's
/// opaque id. Reads the Tenant independently of whatever a screen underneath also reads: the shell
/// has no channel to a screen's own resource state, and duplicating one small GET is cheaper than
/// wiring a shared cache across every screen for this.
function TenantNav({ tenantId, route }: { tenantId: string; route: Route }) {
  const tenant = useResource<Tenant>(
    () => api.GET("/admin/tenants/{id}", { params: { path: { id: tenantId } } }),
    tenantId,
  );
  const section = tenantSectionOf(route);

  const items: { label: string; href: string; section: TenantSection }[] = [
    { label: "Overview", href: `/tenants/${tenantId}`, section: "overview" },
    { label: "Events", href: `/tenants/${tenantId}/events`, section: "events" },
    { label: "Connections", href: `/tenants/${tenantId}/connections`, section: "connections" },
    { label: "Sources", href: `/tenants/${tenantId}/sources`, section: "sources" },
    { label: "Topics", href: `/tenants/${tenantId}/topics`, section: "topics" },
    { label: "API keys", href: `/tenants/${tenantId}/tenant-api-keys`, section: "apiKeys" },
  ];

  return (
    <nav className="topbar-row tenant-row" aria-label="Tenant">
      <span className="tenant-name">
        {tenant.data ? tenant.data.name : tenant.problem ? "This Tenant" : "Loading Tenant…"}
      </span>
      <ul className="nav-list">
        {items.map((item) => (
          <li key={item.section}>
            <Link to={item.href} current={item.section === section}>
              {item.label}
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
        <SubscriptionScreen
          tenantId={route.tenantId}
          topicId={route.topicId}
          subscriptionId={route.subscriptionId}
        />
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
