import { type Mutation, MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";
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

let secretsShown = 0;

/// A one-time secret exists only on screen, and signing out would unmount it. While one is shown a
/// lapsed session waits; closing the last one re-reads the session, so the Operator reaches sign-in then.
export function showSecret(client: QueryClient) {
  secretsShown += 1;
  return () => {
    secretsShown -= 1;
    if (secretsShown === 0) void client.invalidateQueries({ queryKey: ["session"] });
  };
}

/// One client per running dashboard, and one per test, so nothing cached by one leaks into another.
export const createQueryClient = () => {
  /// A 401 from any Admin call means the session lapsed under a signed-in shell. Re-reading the
  /// session lets its own 401 handling sign the Operator out, instead of every screen failing in place.
  /// A write whose answer exists only once holds a lapse as well, until it lands or fails; `settling` is
  /// the write reporting its own failure, which no longer holds anything.
  let lapseHeld = false;
  const held = (settling?: Mutation<unknown, unknown, unknown, unknown>) =>
    secretsShown > 0 ||
    client
      .getMutationCache()
      .findAll({ status: "pending", predicate: (m) => m !== settling && m.meta?.holdsSheet === true }).length > 0;
  const onError = (error: unknown, settling?: Mutation<unknown, unknown, unknown, unknown>) => {
    const lapsed = (error as Problem).status === 401;
    if (lapsed && held(settling)) lapseHeld = true;
    // A held write that fails shows no secret, so a lapse it held is re-checked now rather than on a close.
    else if (lapsed || (lapseHeld && settling?.meta?.holdsSheet === true)) {
      lapseHeld = false;
      void client.invalidateQueries({ queryKey: ["session"] });
    }
  };
  const client = new QueryClient({
    // The session read reports its own 401 as a signed-out answer; re-reading it on its own failure would loop.
    queryCache: new QueryCache({ onError: (error, query) => query.queryKey[0] !== "session" && onError(error) }),
    mutationCache: new MutationCache({ onError: (error, _variables, _context, mutation) => onError(error, mutation) }),
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
