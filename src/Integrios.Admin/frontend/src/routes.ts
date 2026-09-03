import { useSyncExternalStore } from "react";

/// Every route names the Tenant it works inside, because Tenant is an ownership boundary rather
/// than the signed-in User's account. A screen therefore never infers a Tenant from session state:
/// the path is the authoritative selection, and a copied link resolves to the same Tenant.
export type Route =
  | { name: "tenants" }
  | { name: "tenant"; tenantId: string }
  | { name: "connections"; tenantId: string }
  | { name: "connection"; tenantId: string; connectionId: string }
  | { name: "tenantApiKeys"; tenantId: string }
  | { name: "sources"; tenantId: string }
  | { name: "source"; tenantId: string; sourceId: string }
  | { name: "topics"; tenantId: string }
  | { name: "topic"; tenantId: string; topicId: string }
  | { name: "subscription"; tenantId: string; topicId: string; subscriptionId: string }
  | { name: "connectors" }
  | { name: "connector"; connectorId: string }
  | { name: "unknown"; path: string };

const uuid = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

export function parseRoute(path: string): Route {
  const segments = path.split("/").filter(Boolean);
  const unknown: Route = { name: "unknown", path };

  if (segments.length === 0) return { name: "tenants" };

  if (segments[0] === "connectors") {
    if (segments.length === 1) return { name: "connectors" };
    if (segments.length === 2 && uuid.test(segments[1]))
      return { name: "connector", connectorId: segments[1] };
    return unknown;
  }

  if (segments[0] !== "tenants") return unknown;
  if (segments.length === 1) return { name: "tenants" };
  if (!uuid.test(segments[1])) return unknown;
  const tenantId = segments[1];
  if (segments.length === 2) return { name: "tenant", tenantId };

  const [, , capability, id, nested, nestedId] = segments;
  switch (capability) {
    case "connections":
      if (segments.length === 3) return { name: "connections", tenantId };
      if (segments.length === 4 && uuid.test(id)) return { name: "connection", tenantId, connectionId: id };
      return unknown;
    case "tenant-api-keys":
      return segments.length === 3 ? { name: "tenantApiKeys", tenantId } : unknown;
    case "sources":
      if (segments.length === 3) return { name: "sources", tenantId };
      if (segments.length === 4 && uuid.test(id)) return { name: "source", tenantId, sourceId: id };
      return unknown;
    case "topics":
      if (segments.length === 3) return { name: "topics", tenantId };
      if (segments.length === 4 && uuid.test(id)) return { name: "topic", tenantId, topicId: id };
      if (segments.length === 6 && uuid.test(id) && nested === "subscriptions" && uuid.test(nestedId))
        return { name: "subscription", tenantId, topicId: id, subscriptionId: nestedId };
      return unknown;
    default:
      return unknown;
  }
}

const listeners = new Set<() => void>();

function notify() {
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  window.addEventListener("popstate", listener);
  return () => {
    listeners.delete(listener);
    window.removeEventListener("popstate", listener);
  };
}

/// Pushing history directly does not raise `popstate`, so in-app navigation notifies subscribers
/// itself. Back and forward still arrive through the browser's own event.
export function navigate(path: string) {
  if (path === location.pathname) return;
  history.pushState(null, "", path);
  notify();
}

export function useRoute(): Route {
  return parseRoute(useSyncExternalStore(subscribe, () => location.pathname, () => "/"));
}
