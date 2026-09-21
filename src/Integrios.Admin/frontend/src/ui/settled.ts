import { useEffect, useState } from "react";

/// The value once it has stopped changing. A check that calls the Admin API on every keystroke
/// answers a question nobody has finished asking, so the check waits for the Operator to pause.
export function useSettled<T>(value: T, delay: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delay);
    return () => clearTimeout(timer);
  }, [value, delay]);
  return settled;
}
