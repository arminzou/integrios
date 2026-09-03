import type { FetchResult } from "./useCursorList";
import { useResource } from "./useResource";

/// A picker over another capability's list — the Connector a Connection installs, the Connection and
/// Topic a Source belongs to, the destination Connection a Subscription delivers to. It reads one
/// page at the list maximum and reports whether the server had more, so a chooser can say so rather
/// than silently omitting the option the Operator is looking for.
///
/// ponytail: one page of 100, no in-picker paging or search. A deployment that outgrows that needs a
/// searchable picker against a server-side filter, not a longer first page.
export function useOptions<Item>(
  load: () => Promise<FetchResult<{ items: Item[]; next_cursor?: string | null }>>,
  scope: string,
) {
  const { data, busy, problem } = useResource(load, scope);
  return {
    items: data?.items ?? [],
    truncated: Boolean(data?.next_cursor),
    busy,
    problem,
  };
}
