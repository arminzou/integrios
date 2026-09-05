/// The Tenant-scoped sections of the dashboard, named in one place so the contextual navigation row
/// and the breadcrumb cannot drift apart. Kept out of the route table itself because the shell
/// imports it and the route table imports the shell.
export type TenantSection = "overview" | "events" | "connections" | "sources" | "topics" | "apiKeys";

export const sectionLabels: Record<TenantSection, string> = {
  overview: "Overview",
  events: "Events",
  connections: "Connections",
  sources: "Sources",
  topics: "Topics",
  apiKeys: "API keys",
};

export const sectionHrefs: Record<TenantSection, (tenantId: string) => string> = {
  overview: (id) => `/tenants/${id}`,
  events: (id) => `/tenants/${id}/events`,
  connections: (id) => `/tenants/${id}/connections`,
  sources: (id) => `/tenants/${id}/sources`,
  topics: (id) => `/tenants/${id}/topics`,
  apiKeys: (id) => `/tenants/${id}/tenant-api-keys`,
};

export const sectionOrder: TenantSection[] = ["overview", "events", "connections", "sources", "topics", "apiKeys"];
