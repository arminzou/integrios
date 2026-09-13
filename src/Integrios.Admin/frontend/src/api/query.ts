import { QueryClient } from "@tanstack/react-query";
import { type Problem, problemFrom } from "./problem";

/// The shape openapi-fetch returns for one call.
type FetchResult<T> = {
  data?: T | undefined;
  error?: unknown;
  response: { status: number };
};

type CursorPage<Item> = { items: Item[]; next_cursor?: string | null };

/// The one adapter between the typed client and TanStack Query: a call the Admin API refused
/// becomes a thrown `Problem`, which is what every `error` in this dashboard is. Screens therefore
/// read `query.error as Problem | null` and hand it to the same `formError` and `applyProblem` they
/// always used, rather than re-deriving Problem Details per call site.
export async function call<T>(request: () => Promise<FetchResult<T>>): Promise<T> {
  const { data, error, response } = await request();
  if (response.status < 200 || response.status >= 300) throw problemFrom(error, response.status);
  // A successful deactivate, revoke, or replay answers with no body at all.
  return data as T;
}

export const asProblem = (error: unknown): Problem | null => (error as Problem | null) ?? null;

/// Every Admin list is an opaque forward-only cursor: no total, no page number, no offset, and an
/// explicit Load more rather than infinite scroll. The cursor is only valid for the filters it was
/// issued under, so the filters belong in the query key — a changed scope is a different query with
/// its own pages, never the previous scope's rows relabelled.
export const nextCursor = <Item>(page: CursorPage<Item>) => page.next_cursor ?? undefined;

/// One client per running dashboard, and one per test, so nothing cached by one leaks into another.
export const createQueryClient = () =>
  new QueryClient({
    defaultOptions: {
      queries: {
        // The dashboard reports authoritative server state, and an Operator acting on a stale count
        // is the failure mode that matters; a read is cheap by comparison. Refetch on focus is off
        // because a background tab regaining focus is not a reason to move what is under the pointer.
        staleTime: 0,
        refetchOnWindowFocus: false,
        // The Admin API answers a refused request with Problem Details, not with a transient failure
        // worth retrying; a retry would only delay the message the Operator has to read.
        retry: false,
      },
    },
  });
