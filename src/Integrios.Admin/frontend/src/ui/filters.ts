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
