import { useCallback, useRef, useState } from "react";
import { type Problem, problemFrom } from "../api/problem";
import type { FetchResult } from "./useCursorList";

/// One in-flight write and whatever the server said about it. A failed write leaves the form filled
/// in and shows the returned Problem Details; it never clears the Operator's input or reports a
/// success the server did not give.
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const running = useRef(false);

  const run = useCallback(async <T>(call: () => Promise<FetchResult<T>>, onSuccess?: (data: T | undefined) => void) => {
    if (running.current) return false;
    running.current = true;
    setBusy(true);
    setProblem(null);

    try {
      const { data, error, response } = await call();
      // A successful revoke or deactivate answers with no body, so the status is what decides the
      // outcome rather than the presence of parsed data.
      if (response.status < 200 || response.status >= 300) {
        setProblem(problemFrom(error, response.status));
        return false;
      }
      onSuccess?.(data);
      return true;
    } finally {
      running.current = false;
      setBusy(false);
    }
  }, []);

  return { busy, problem, setProblem, run };
}
