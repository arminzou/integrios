import { useCallback, useRef, useState } from "react";
import { type Problem, problemFrom } from "../api/problem";
import type { FetchResult } from "./useCursorList";

/// One in-flight write and whatever the server said about it. A failed write leaves the form filled
/// in and shows the returned Problem Details; it never clears the Operator's input or reports a
/// success the server did not give.
///
/// `run` answers with the Problem, or `null` when the write succeeded, so a caller that has to route
/// the failure somewhere — a form putting each message beside its own control — can do it where it
/// submitted rather than by watching state change.
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const running = useRef(false);

  const run = useCallback(
    async <T>(
      call: () => Promise<FetchResult<T>>,
      onSuccess?: (data: T | undefined) => void,
    ): Promise<Problem | null> => {
      if (running.current) return null;
      running.current = true;
      setBusy(true);
      setProblem(null);

      try {
        const { data, error, response } = await call();
        // A successful revoke or deactivate answers with no body, so the status is what decides the
        // outcome rather than the presence of parsed data.
        if (response.status < 200 || response.status >= 300) {
          const failure = problemFrom(error, response.status);
          setProblem(failure);
          return failure;
        }
        onSuccess?.(data);
        return null;
      } finally {
        running.current = false;
        setBusy(false);
      }
    },
    [],
  );

  return { busy, problem, setProblem, run };
}
