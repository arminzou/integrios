import { useCallback, useEffect, useRef, useState } from "react";
import { type Problem, problemFrom } from "../api/problem";
import type { FetchResult } from "./useCursorList";

/// One authoritative read of a single resource. `scope` identifies what is being read — the route
/// values that select it — so navigating to a sibling resource re-reads rather than showing the
/// previous one while the new read is still in flight.
export function useResource<T>(load: () => Promise<FetchResult<T>>, scope: string) {
  const loadRef = useRef(load);
  loadRef.current = load;
  const [data, setData] = useState<T | null>(null);
  const [busy, setBusy] = useState(true);
  const [problem, setProblem] = useState<Problem | null>(null);
  const generation = useRef(0);

  const run = useCallback(async () => {
    const reading = ++generation.current;
    setBusy(true);
    const { data: body, error, response } = await loadRef.current();
    if (generation.current !== reading) return;

    setBusy(false);
    if (response.status < 200 || response.status >= 300) {
      setProblem(problemFrom(error, response.status));
      return;
    }
    setProblem(null);
    setData(body ?? null);
  }, []);

  useEffect(() => {
    generation.current++;
    setData(null);
    setProblem(null);
    void run();
  }, [scope, run]);

  return { data, busy, problem, reload: run };
}
