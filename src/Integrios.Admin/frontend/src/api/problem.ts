/// The Admin API reports every failure as RFC 7807 Problem Details, and validation failures carry
/// an `errors` map keyed by the request field that was rejected. Screens render a field's message
/// beside that field and everything else as one form-level error, so a failure is never a silent
/// no-op and never lands only in a colour change.
export type Problem = {
  status: number;
  detail?: string;
  errors: Record<string, string[]>;
};

type ProblemBody = {
  title?: unknown;
  detail?: unknown;
  errors?: unknown;
};

export function problemFrom(body: unknown, status: number): Problem {
  const parsed = (typeof body === "object" && body !== null ? body : {}) as ProblemBody;
  const detail = [parsed.detail, parsed.title].find(
    (value): value is string => typeof value === "string" && value.length > 0,
  );
  const errors: Record<string, string[]> = {};

  if (typeof parsed.errors === "object" && parsed.errors !== null)
    for (const [field, messages] of Object.entries(parsed.errors as Record<string, unknown>))
      if (Array.isArray(messages) && messages.length > 0) errors[field.toLowerCase()] = messages.map(String);

  return { status, detail, errors };
}

/// Field keys come from server-side validation, which names them in its own casing. Matching
/// case-insensitively keeps a message beside its control instead of silently demoting it to a
/// form-level error when the casing differs from the request body.
export function fieldError(problem: Problem | null, field: string): string | undefined {
  return problem?.errors[field.toLowerCase()]?.[0];
}

/// Everything the server reported that no rendered field will show. A message attributed to a field
/// this screen does not render would otherwise disappear entirely, and a failure with no message at
/// all still has to say that it failed.
export function formError(problem: Problem | null, renderedFields: readonly string[] = []): string | undefined {
  if (!problem) return undefined;
  const rendered = new Set(renderedFields.map((field) => field.toLowerCase()));
  const unattributed = Object.entries(problem.errors)
    .filter(([field]) => field === "" || !rendered.has(field))
    .flatMap(([, messages]) => messages);
  const parts = [problem.detail, ...unattributed].filter(Boolean) as string[];

  if (parts.length > 0) return parts.join(" ");
  // Every message is already beside its own control; adding a form-level echo would only repeat it.
  return Object.keys(problem.errors).length > 0 ? undefined : `The request failed (${problem.status}).`;
}
