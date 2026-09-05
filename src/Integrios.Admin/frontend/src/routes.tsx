import type { ReactNode } from "react";
import { Link, type RouteObject, useLocation, useParams } from "react-router";
import { App } from "./App";
import { isIdentifier } from "./identifiers";
import { ConnectionScreen, ConnectionsScreen } from "./screens/Connections";
import { ConnectorScreen, ConnectorsScreen } from "./screens/Connectors";
import { EventsScreen } from "./screens/Events";
import { SourceScreen, SourcesScreen } from "./screens/Sources";
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

export const routeConfig: RouteObject[] = [
  {
    // A pathless layout route: `App` owns the session bootstrap and the signed-in shell, and every
    // screen below renders into its outlet.
    element: <App />,
    children: [
      { index: true, element: <TenantsScreen /> },
      { path: "tenants", element: <TenantsScreen /> },
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
            {({ tenantId, connectionId }) => <ConnectionScreen tenantId={tenantId} connectionId={connectionId} />}
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
        element: <Ids>{({ tenantId, sourceId }) => <SourceScreen tenantId={tenantId} sourceId={sourceId} />}</Ids>,
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
      { path: "connectors", element: <ConnectorsScreen /> },
      {
        path: "connectors/:connectorId",
        element: <Ids>{({ connectorId }) => <ConnectorScreen connectorId={connectorId} />}</Ids>,
      },
      { path: "*", element: <NotFound /> },
    ],
  },
];
