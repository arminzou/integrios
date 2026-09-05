import type { FieldValues, Path, UseFormReturn } from "react-hook-form";
import { fieldError, type Problem } from "../api/problem";

/// Puts a rejected write back on the form it came from: every message the Admin API attributed to a
/// field lands on that field's own control, where React Hook Form renders it and points the control
/// at it. Whatever the server attributed to no rendered field stays the form-level error, which the
/// screen reads from `formError`, so a message is never lost between the two.
///
/// The next validation clears these again: they are the server's verdict on one submission, not a
/// rule the form can check on its own.
export function applyProblem<TValues extends FieldValues>(
  form: UseFormReturn<TValues, unknown, unknown>,
  failure: unknown,
  fields: readonly Path<TValues>[],
) {
  // Whatever a mutation rejected with: everything the Admin API refuses arrives here as a Problem,
  // and anything else — a bug thrown inside the client — has no field messages to place.
  const problem = failure as Problem | null;
  if (!problem?.errors) return;

  for (const field of fields) {
    const message = fieldError(problem, field);
    if (message) form.setError(field, { type: "server", message });
  }
}
