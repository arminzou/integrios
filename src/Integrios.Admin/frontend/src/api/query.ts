import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";
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
  // A successful revoke or replay answers with no body at all.
  return data as T;
}

export const asProblem = (error: unknown): Problem | null => (error as Problem | null) ?? null;

/// Every Admin list is an opaque forward-only cursor: no total, no page number, no offset, and an
/// explicit Load more rather than infinite scroll. The cursor is only valid for the filters it was
/// issued under, so the filters belong in the query key — a changed scope is a different query with
/// its own pages, never the previous scope's rows relabelled.
export const nextCursor = <Item>(page: CursorPage<Item>) => page.next_cursor ?? undefined;

/// One client per running dashboard, and one per test, so nothing cached by one leaks into another.
export const createQueryClient = () => {
  /// A 401 from any Admin call means the session lapsed under a signed-in shell. Re-reading the
  /// session lets its own 401 handling sign the Operator out, instead of every screen failing in place.
  const onError = (error: unknown) => {
    if ((error as Problem).status === 401) void client.invalidateQueries({ queryKey: ["session"] });
  };
  const client = new QueryClient({
    // The session read reports its own 401 as a signed-out answer; re-reading it on its own failure would loop.
    queryCache: new QueryCache({ onError: (error, query) => query.queryKey[0] !== "session" && onError(error) }),
    mutationCache: new MutationCache({ onError }),
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
  return client;
};
