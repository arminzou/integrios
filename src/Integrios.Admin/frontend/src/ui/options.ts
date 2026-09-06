import { useQuery } from "@tanstack/react-query";
import { api } from "../api/client";
import { call } from "../api/query";

/// The lists a screen reads to turn the identifiers the Admin API returns into the names an Operator
/// recognises, and to fill the pickers an authoring form offers. One read per list per Tenant, shared
/// by query key, so naming the rows of a list costs no request the create panel beside it was not
/// already making.
///
/// They are read unfiltered on purpose. A picker offers only what can still be chosen and narrows
/// these to `active` itself, but a name has to resolve for every row a list can show — including the
/// deactivated Connection a Source is still bound to, which is exactly the row an Operator is most
/// likely to be looking up. Reading them filtered under a key that did not say so is what let the
/// Event ledger and the Sources create panel share `topic-options` while asking for different rows:
/// whichever mounted first decided what the other saw.
///
/// The first hundred, matching what the pickers have always offered. Beyond that a name simply does
/// not resolve and the identifier is shown instead, which is the same thing the screens did before.
const limit = 100;

export function useConnectionOptions(tenantId: string) {
  return useQuery({
    queryKey: ["connection-options", tenantId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/connections", { params: { path: { tenantId }, query: { limit } } }),
      ),
  });
}

export function useTopicOptions(tenantId: string) {
  return useQuery({
    queryKey: ["topic-options", tenantId],
    queryFn: () =>
      call(() => api.GET("/admin/tenants/{tenantId}/topics", { params: { path: { tenantId }, query: { limit } } })),
  });
}

export function useConnectorOptions() {
  return useQuery({
    queryKey: ["connector-options"],
    queryFn: () => call(() => api.GET("/admin/connectors", { params: { query: { limit } } })),
  });
}

/// The name a row's identifier stands for, or the identifier itself when this list cannot name it.
/// Falling back to the identifier rather than to a placeholder keeps the row usable while the list
/// is still in flight, and keeps it honest when the thing it names is outside the first hundred.
export function nameIn(items: { id: string; name: string }[] | undefined, id: string): string {
  return items?.find((item) => item.id === id)?.name ?? id;
}

/// A picker offers what can still be chosen. Naming a row is a different question from choosing one,
/// which is why the reads above answer both rather than one at the other's expense.
export function activeOnly<T extends { status: string }>(items: T[] | undefined): T[] {
  return (items ?? []).filter((item) => item.status === "active");
}
