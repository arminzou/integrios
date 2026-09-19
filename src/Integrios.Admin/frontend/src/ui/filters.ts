import { useSearchParams } from "react-router";

/// A list filter that lives in the URL rather than in component state. A filtered view is then
/// something an Operator can send to a colleague, and the back button restores the previous scope
/// instead of leaving the list entirely — the same contract the Event selection already has through
/// its route.
///
/// The cursor behaviour is unchanged: the value still reaches the query key, so a changed filter is
/// still a different query with its own pages rather than the previous scope's rows relabelled.
///
/// An empty value deletes the parameter instead of writing `?status=`, so clearing a filter leaves
/// the URL exactly as short as it was before the filter was ever set.
export function useFilterParam(name: string): [string, (value: string) => void] {
  const [params, setParams] = useSearchParams();

  const set = (value: string) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (value) next.set(name, value);
      else next.delete(name);
      return next;
    });

  return [params.get(name) ?? "", set];
}

/// The filters of one list, read from the URL. A screen declares its parameters spelled as its Admin
/// API spells them, so the URL, the query key and the request share one name and an inbound link
/// keeps working.
///
/// `set` writes one history entry, so Back undoes the last change. `values` is what a query key
/// carries: a changed filter is a different query with its own pages. `clear` removes only the
/// declared parameters, so a parameter that is not a filter survives it.
export function useListFilters<const TName extends string>(names: readonly TName[]) {
  const [params, setParams] = useSearchParams();
  const values = Object.fromEntries(names.map((name) => [name, params.get(name) ?? ""])) as Record<TName, string>;

  const write = (change: (next: URLSearchParams) => void) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      change(next);
      return next;
    });

  return {
    values,
    applied: names.filter((name) => values[name] !== "").length,
    set: (name: TName, value: string) => write((next) => (value ? next.set(name, value) : next.delete(name))),
    clear: () =>
      write((next) => {
        for (const name of names) next.delete(name);
      }),
  };
}
