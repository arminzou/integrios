import { useSearchParams } from "react-router";

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

  const patch = (changes: Partial<Record<TName, string>>, options?: { replace?: boolean }) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      for (const [name, value] of Object.entries<string | undefined>(changes)) {
        if (value) next.set(name, value);
        else next.delete(name);
      }
      return next;
    }, options);

  return {
    values,
    applied: names.filter((name) => values[name] !== "").length,
    /// Several parameters in one history entry, as a range's two ends are; `replace` overwrites the
    /// current entry instead, for a selection that is still being extended.
    patch,
    set: (name: TName, value: string) => patch({ [name]: value } as Partial<Record<TName, string>>),
    clear: () =>
      setParams((current) => {
        const next = new URLSearchParams(current);
        for (const name of names) next.delete(name);
        return next;
      }),
  };
}
