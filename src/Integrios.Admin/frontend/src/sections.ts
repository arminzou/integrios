/// The Tenant-scoped sections of the dashboard, named in one place so the contextual navigation row
/// and the breadcrumb cannot drift apart. Kept out of the route table itself because the shell
/// imports it and the route table imports the shell.
export type TenantSection = "overview" | "events" | "destinations" | "sources" | "topics" | "subscriptions" | "apiKeys";

export const sectionLabels: Record<TenantSection, string> = {
  overview: "Overview",
  events: "Events",
  destinations: "Destinations",
  sources: "Sources",
  topics: "Topics",
  subscriptions: "Subscriptions",
  apiKeys: "API keys",
};

export const sectionHrefs: Record<TenantSection, (tenantId: string) => string> = {
  overview: (id) => `/tenants/${id}`,
  events: (id) => `/tenants/${id}/events`,
  destinations: (id) => `/tenants/${id}/destinations`,
  sources: (id) => `/tenants/${id}/sources`,
  topics: (id) => `/tenants/${id}/topics`,
  subscriptions: (id) => `/tenants/${id}/subscriptions`,
  apiKeys: (id) => `/tenants/${id}/tenant-api-keys`,
};

/// The Tenant sections in the order an Operator authors them, grouped into what they are for: the
/// landing (Overview), the authoring run, the observe run, and API keys unlabelled at the end. The
/// shell reads the groups to put a label above the authoring and observe runs.
export const sectionGroups: { label?: string; sections: TenantSection[] }[] = [
  { sections: ["overview"] },
  { label: "Author", sections: ["sources", "topics", "destinations", "subscriptions"] },
  { label: "Observe", sections: ["events"] },
  { sections: ["apiKeys"] },
];
