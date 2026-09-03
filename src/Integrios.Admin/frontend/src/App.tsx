import { useEffect, useState, type ReactNode } from "react";
import { loadSession, signInHref, type OperatorSession } from "./api/client";
import { useRoute, type Route } from "./routes";
import { Link } from "./ui/controls";
import { ConnectorScreen, ConnectorsScreen } from "./screens/Connectors";
import { ConnectionScreen, ConnectionsScreen } from "./screens/Connections";
import { EventScreen, EventsScreen } from "./screens/Events";
import { SourceScreen, SourcesScreen } from "./screens/Sources";
import { SubscriptionScreen } from "./screens/Subscriptions";
import { TenantApiKeysScreen } from "./screens/TenantApiKeys";
import { TenantScreen, TenantsScreen } from "./screens/Tenants";
import { TopicScreen, TopicsScreen } from "./screens/Topics";

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

function Shell({ children }: { children: ReactNode }) {
  return (
    <main>
      <h1>Integrios Operator</h1>
      {children}
    </main>
  );
}

function SignedIn({ session }: { session: OperatorSession }) {
  const route = useRoute();

  return (
    <>
      <header>
        <nav aria-label="Capabilities">
          <ul>
            <li>
              <Link to="/tenants">Tenants</Link>
            </li>
            <li>
              <Link to="/connectors">Connectors</Link>
            </li>
          </ul>
        </nav>
        <p>
          Signed in as <strong>{session.display_name}</strong>
          {session.email ? ` (${session.email})` : ""}.
        </p>
        {/* A native form submission carries no custom header, so the antiforgery token must
            travel through the server-configured form field rather than the header name used by
            the typed client's own requests. */}
        <form method="post" action="/auth/logout">
          <input type="hidden" name={session.antiforgery_form_field_name} value={session.antiforgery_token} />
          <button type="submit">Sign out</button>
        </form>
      </header>
      <main>
        <Screen route={route} />
      </main>
    </>
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
      return <EventScreen tenantId={route.tenantId} eventId={route.eventId} />;
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
