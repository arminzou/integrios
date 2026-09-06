import type { ReactNode } from "react";
import { isRouteErrorResponse, Link, type RouteObject, useLocation, useParams, useRouteError } from "react-router";
import { App } from "./App";
import { isIdentifier } from "./identifiers";
import { ConnectionsScreen } from "./screens/Connections";
import { ConnectorScreen, ConnectorsScreen } from "./screens/Connectors";
import { EventsScreen } from "./screens/Events";
import { SourcesScreen } from "./screens/Sources";
import { SubscriptionScreen } from "./screens/Subscriptions";
import { TenantApiKeysScreen } from "./screens/TenantApiKeys";
import { TenantScreen, TenantsScreen } from "./screens/Tenants";
import { TopicScreen, TopicsScreen } from "./screens/Topics";
import type { TenantSection } from "./sections";

/// Every route names the Tenant it works inside, because Tenant is an ownership boundary rather
/// than the signed-in User's account. A screen therefore never infers a Tenant from session state:
/// the path is the authoritative selection, and a copied link resolves to the same Tenant.
///
/// The `section` handle is what the shell reads to name the current Tenant section in its
/// breadcrumb; navigation marks its own active destination through `NavLink`.
/// Refuses a route value that is not an identifier before the screen below it can turn one into an
/// Admin request. See `isIdentifier`.
function Ids({ children }: { children: (ids: Record<string, string>) => ReactNode }) {
  const params = useParams();
  const ids = Object.entries(params).filter(([key]) => key !== "*");

  if (ids.some(([, value]) => !isIdentifier(value))) return <NotFound />;
  return <>{children(Object.fromEntries(ids) as Record<string, string>)}</>;
}

function NotFound() {
  const { pathname } = useLocation();
  return (
    <>
      <h1>Not found</h1>
      <p role="alert">
        No Operator screen owns <code>{pathname}</code>.
      </p>
      <p>
        <Link to="/tenants">Go to Tenants</Link>
      </p>
    </>
  );
}

/// The last line of defence. A screen that throws while rendering would otherwise leave a blank
/// document with nothing to act on; this replaces the layout route, so it carries no shell and
/// recovers through a full document load rather than a router navigation the failure may have
/// already broken.
function RouteError() {
  const error = useRouteError();
  const detail = isRouteErrorResponse(error)
    ? `${error.status} ${error.statusText}`
    : error instanceof Error
      ? error.message
      : null;

  return (
    <main id="main" tabIndex={-1}>
      <h1>This screen stopped rendering</h1>
      <p role="alert">The dashboard could not finish drawing this page.{detail ? ` ${detail}` : ""}</p>
      <p>
        <a href="/tenants">Reload the Tenants list</a>
      </p>
    </main>
  );
}

export const routeConfig: RouteObject[] = [
  {
    // A pathless layout route: `App` owns the session bootstrap and the signed-in shell, and every
    // screen below renders into its outlet.
    element: <App />,
    errorElement: <RouteError />,
    children: [
      { index: true, handle: { title: "Tenants" }, element: <TenantsScreen /> },
      { path: "tenants", handle: { title: "Tenants" }, element: <TenantsScreen /> },
      {
        path: "tenants/:tenantId",
        handle: { section: "overview" satisfies TenantSection },
        element: <Ids>{({ tenantId }) => <TenantScreen tenantId={tenantId} />}</Ids>,
      },
      {
        path: "tenants/:tenantId/connections",
        handle: { section: "connections" satisfies TenantSection },
        element: <Ids>{({ tenantId }) => <ConnectionsScreen tenantId={tenantId} />}</Ids>,
      },
      {
        path: "tenants/:tenantId/connections/:connectionId",
        handle: { section: "connections" satisfies TenantSection },
        element: (
          <Ids>
            {({ tenantId, connectionId }) => (
              <ConnectionsScreen tenantId={tenantId} selectedConnectionId={connectionId} />
            )}
          </Ids>
        ),
      },
      {
        path: "tenants/:tenantId/events",
        handle: { section: "events" satisfies TenantSection },
        element: <Ids>{({ tenantId }) => <EventsScreen tenantId={tenantId} />}</Ids>,
      },
      {
        path: "tenants/:tenantId/events/:eventId",
        handle: { section: "events" satisfies TenantSection },
        element: <Ids>{({ tenantId, eventId }) => <EventsScreen tenantId={tenantId} selectedEventId={eventId} />}</Ids>,
      },
      {
        path: "tenants/:tenantId/tenant-api-keys",
        handle: { section: "apiKeys" satisfies TenantSection },
        element: <Ids>{({ tenantId }) => <TenantApiKeysScreen tenantId={tenantId} />}</Ids>,
      },
      {
        path: "tenants/:tenantId/sources",
        handle: { section: "sources" satisfies TenantSection },
        element: <Ids>{({ tenantId }) => <SourcesScreen tenantId={tenantId} />}</Ids>,
      },
      {
        path: "tenants/:tenantId/sources/:sourceId",
        handle: { section: "sources" satisfies TenantSection },
        element: (
          <Ids>{({ tenantId, sourceId }) => <SourcesScreen tenantId={tenantId} selectedSourceId={sourceId} />}</Ids>
        ),
      },
      {
        path: "tenants/:tenantId/topics",
        handle: { section: "topics" satisfies TenantSection },
        element: <Ids>{({ tenantId }) => <TopicsScreen tenantId={tenantId} />}</Ids>,
      },
      {
        path: "tenants/:tenantId/topics/:topicId",
        handle: { section: "topics" satisfies TenantSection },
        element: <Ids>{({ tenantId, topicId }) => <TopicScreen tenantId={tenantId} topicId={topicId} />}</Ids>,
      },
      {
        path: "tenants/:tenantId/topics/:topicId/subscriptions/:subscriptionId",
        handle: { section: "topics" satisfies TenantSection },
        element: (
          <Ids>
            {({ tenantId, topicId, subscriptionId }) => (
              <SubscriptionScreen tenantId={tenantId} topicId={topicId} subscriptionId={subscriptionId} />
            )}
          </Ids>
        ),
      },
      { path: "connectors", handle: { title: "Connectors" }, element: <ConnectorsScreen /> },
      {
        path: "connectors/:connectorId",
        handle: { title: "Connector" },
        element: <Ids>{({ connectorId }) => <ConnectorScreen connectorId={connectorId} />}</Ids>,
      },
      { path: "*", handle: { title: "Not found" }, element: <NotFound /> },
    ],
  },
];
