import { useCallback, useEffect, useRef, useState } from "react";
import { type Problem, problemFrom } from "../api/problem";

/// The shape openapi-fetch returns for one call. Declaring it here keeps the hook framework-neutral
/// instead of importing the client's own generics into every screen.
export type FetchResult<T> = {
  data?: T | undefined;
  error?: unknown;
  response: { status: number };
};

type Page<Item> = { items: Item[]; next_cursor?: string | null };

type State<Item> = {
  items: Item[];
  cursor: string | null;
  busy: boolean;
  loaded: boolean;
  problem: Problem | null;
};

function initialState<Item>(): State<Item> {
  return { items: [], cursor: null, busy: true, loaded: false, problem: null };
}

/// Every Admin list is an opaque forward-only cursor: no total, no page number, no offset, and an
/// explicit Load more rather than infinite scroll. `scope` is whatever identifies the list being
/// read — its route and its active filters. When it changes the accumulated results are discarded
/// and reading restarts from the first cursor, because a cursor is only valid for the filters it
/// was issued under and the server rejects it otherwise instead of silently resetting.
export function useCursorList<Item>(load: (after: string | null) => Promise<FetchResult<Page<Item>>>, scope: string) {
  const loadRef = useRef(load);
  loadRef.current = load;
  const [state, setState] = useState<State<Item>>(initialState<Item>);
  // Only the newest read may write state: a slow first page must not overwrite the results of the
  // filter the Operator has already moved on to.
  const generation = useRef(0);

  const run = useCallback(async (after: string | null) => {
    const reading = ++generation.current;
    setState((previous) => ({ ...previous, busy: true, problem: null }));

    const { data, error, response } = await loadRef.current(after);
    if (generation.current !== reading) return;

    if (!data) {
      setState((previous) => ({
        ...previous,
        busy: false,
        loaded: true,
        problem: problemFrom(error, response.status),
      }));
      return;
    }

    setState((previous) => ({
      items: after === null ? data.items : [...previous.items, ...data.items],
      cursor: data.next_cursor ?? null,
      busy: false,
      loaded: true,
      problem: null,
    }));
  }, []);

  useEffect(() => {
    generation.current++;
    setState(initialState<Item>());
    void run(null);
  }, [scope, run]);

  // Read through a ref rather than a state updater: an updater is invoked twice under StrictMode,
  // which would append the same page twice.
  const stateRef = useRef(state);
  stateRef.current = state;

  const loadMore = useCallback(() => {
    const { cursor, busy } = stateRef.current;
    if (cursor && !busy) void run(cursor);
  }, [run]);

  /// Re-read authoritative server state after a mutation rather than patching a second local copy.
  const reload = useCallback(() => void run(null), [run]);

  return { ...state, loadMore, reload };
}
