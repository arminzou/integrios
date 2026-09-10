import { type FieldMapping, payloadFieldPaths } from "../ui/fieldMapping";

/// The guided half of the Integrios Event Builder: the choices an Operator makes about a
/// provider-native request, from which the one persisted artefact — a JSONata expression in the
/// existing versioned envelope — is generated. Nothing here is stored: the expression is.
export type GuidedMapping = {
  eventPrefix: string;
  /// "" means the value is not taken from a header.
  eventHeader: string;
  actionPath: string;
  identityHeader: string;
  requireIdentity: boolean;
  payloadMode: "entire" | "fields";
  payloadRows: FieldMapping[];
};

export const emptyGuided: GuidedMapping = {
  eventPrefix: "",
  eventHeader: "",
  actionPath: "",
  identityHeader: "",
  requireIdentity: true,
  payloadMode: "entire",
  payloadRows: [],
};

/// A header is addressed through the bounded webhook context the Ingestion path builds: lower-cased
/// request headers under `$context.headers`, and nothing else. Names are backtick-quoted because a
/// header name is not a JSONata identifier. A name carrying a backtick has no valid quoting at all —
/// JSONata processes no escapes inside one — so `headerContext` refuses it rather than escaping it
/// into an expression that cannot compile.
export function headerReference(name: string): string {
  return `$context.headers.\`${name}\``;
}

export function normalizeHeaderName(name: string): string {
  return name.trim().toLowerCase();
}

/// The representative headers, as the runtime would present them. Throws the message an Operator has
/// to act on, because a duplicate or unnamed row cannot be resolved into one context object.
export function headerContext(rows: { name: string; value: string }[]): Record<string, string> {
  const headers: Record<string, string> = {};
  for (const row of rows) {
    const name = normalizeHeaderName(row.name);
    if (name === "") throw new Error("Enter a name for every header row.");
    if (name.includes("`")) throw new Error(`${name} cannot be used: a header name cannot contain a backtick.`);
    if (Object.hasOwn(headers, name)) throw new Error(`${name} is listed more than once.`);
    headers[name] = row.value;
  }
  return headers;
}

const completeRows = (rows: FieldMapping[]): FieldMapping[] =>
  rows.filter((row) => row.output.trim() !== "" && row.source !== "");

const payloadObject = (rows: FieldMapping[]): string =>
  `{ ${completeRows(rows)
    .map((row) => `${JSON.stringify(row.output.trim())}: ${row.source}`)
    .join(", ")} }`;

/// Payload field names that appear more than once. JSONata refuses an object constructor with two
/// identical keys at evaluation time, so this is a contract that would fail on every request rather
/// than a cosmetic duplicate.
export function duplicatePayloadFields(rows: FieldMapping[]): string[] {
  const names = completeRows(rows).map((row) => row.output.trim());
  return [...new Set(names.filter((name, index) => names.indexOf(name) !== index))];
}

/// Generates the expression the runtime evaluator actually runs. The guided choices are never
/// persisted beside it — this text is the whole contract, so an expression that no longer matches
/// what these choices would generate is one the guided form can no longer claim to represent.
export function guidedExpression(guided: GuidedMapping): string {
  const assignments: string[] = [];
  if (guided.eventHeader) assignments.push(`$event := ${headerReference(guided.eventHeader)}`);
  if (guided.identityHeader) assignments.push(`$delivery := ${headerReference(guided.identityHeader)}`);
  if (guided.actionPath) assignments.push(`$action := ${guided.actionPath}`);

  const prefix = guided.eventPrefix.trim();
  const base = guided.eventHeader
    ? prefix
      ? `${JSON.stringify(`${prefix}.`)} & $event`
      : "$event"
    : JSON.stringify(prefix);
  const eventType = guided.actionPath ? `${base} & ($exists($action) and $action != "" ? "." & $action : "")` : base;

  const fields = [`"event_type": ${eventType}`];
  if (guided.identityHeader) fields.push('"source_event_id": $delivery');
  fields.push(`"payload": ${guided.payloadMode === "entire" ? "$" : payloadObject(guided.payloadRows)}`);
  const output = `{ ${fields.join(", ")} }`;

  const required: string[] = [];
  if (guided.eventHeader) required.push('$exists($event) and $event != ""');
  if (guided.identityHeader && guided.requireIdentity) required.push('$exists($delivery) and $delivery != ""');

  if (assignments.length === 0 && required.length === 0) return output;
  const body =
    required.length === 0
      ? output
      : `${required.join(" and ")} ? ${output} : $error("This request is missing a value the Source contract requires.")`;
  return assignments.length === 0 ? `(${body})` : `(${assignments.join("; ")}; ${body})`;
}

export type RequirementType = "string" | "number" | "integer" | "boolean";

export const requirementTypes: RequirementType[] = ["string", "number", "integer", "boolean"];

export type InputRequirement = { field: string; type: RequirementType | "" };

/// The fields a requirement row may name. The manifest's input schema is a flat object of scalar
/// properties — the Admin API rejects nested object schemas and has no array type — so a nested or
/// non-scalar value is not offered rather than authored into a manifest that would be refused.
export function requirableFields(body: unknown): string[] {
  if (body === null || typeof body !== "object" || Array.isArray(body)) return [];
  return Object.entries(body)
    .filter(([, value]) => value !== null && typeof value !== "object")
    .map(([name]) => name);
}

export function matchesRequirementType(value: unknown, type: RequirementType | ""): boolean {
  if (type === "integer") return Number.isInteger(value);
  if (type === "number") return typeof value === "number" && Number.isFinite(value);
  return typeof value === type;
}

/// The persisted half: runtime JSON Schema validation applied to every request the Source accepts,
/// not to the sample it was configured from. No rows means no schema at all, which is a real choice
/// — the contract then validates nothing before mapping.
export function requirementsSchema(rows: InputRequirement[]): Record<string, unknown> | undefined {
  const complete = rows.filter((row) => row.field !== "" && row.type !== "");
  if (complete.length === 0) return undefined;
  return {
    type: "object",
    properties: Object.fromEntries(complete.map((row) => [row.field, { type: row.type }])),
    required: complete.map((row) => row.field),
    // The provider sends far more than the Operator declared, and none of it is a reason to reject.
    additionalProperties: true,
  };
}

export type Completion = { label: string; insert: string; detail: string; cursorBack?: number };

/// The only functions offered are ones the runtime evaluator really has: it registers no functions
/// of its own and removes none, so this is a small safe subset of the stock JSONata library, plus
/// the one variable Ingestion binds.
const functions: Completion[] = [
  { label: "$context", insert: "$context", detail: "Bounded Source context" },
  { label: "$exists()", insert: "$exists()", detail: "Test whether a value exists", cursorBack: 1 },
  { label: "$error()", insert: "$error()", detail: "Reject the request with a message", cursorBack: 1 },
  { label: "$join()", insert: "$join()", detail: "Join strings", cursorBack: 1 },
  { label: "$lowercase()", insert: "$lowercase()", detail: "Convert text to lowercase", cursorBack: 1 },
  { label: "$uppercase()", insert: "$uppercase()", detail: "Convert text to uppercase", cursorBack: 1 },
  { label: "$string()", insert: "$string()", detail: "Convert a value to text", cursorBack: 1 },
  { label: "$number()", insert: "$number()", detail: "Convert a value to a number", cursorBack: 1 },
];

/// Completion over the text before the caret. It offers the representative request's own paths and
/// the bounded context, so what it suggests is what this Source could actually read — an authoring
/// aid, never an extension of the runtime environment.
export function completionsAt(
  text: string,
  caret: number,
  sample: { headers: Record<string, string>; body: unknown },
): { start: number; items: Completion[] } {
  const before = text.slice(0, caret);

  const header = before.match(/\$context\.headers\.(`?)([A-Za-z0-9_-]*)$/);
  if (header)
    return {
      // The backtick the Operator has already typed is part of what the suggestion replaces.
      start: caret - header[1].length - header[2].length,
      items: Object.entries(sample.headers)
        .filter(([name]) => name.startsWith(header[2].toLowerCase()))
        .map(([name, value]) => ({ label: `\`${name}\``, insert: `\`${name}\``, detail: value })),
    };

  const context = before.match(/\$context\.([A-Za-z_]*)$/);
  if (context)
    return {
      start: caret - context[1].length,
      items: "headers".startsWith(context[1])
        ? [{ label: "headers", insert: "headers", detail: "Lower-cased request headers" }]
        : [],
    };

  const dollar = before.match(/\$([A-Za-z]*)$/);
  if (dollar)
    return {
      start: caret - dollar[0].length,
      items: functions.filter((item) => item.label.slice(1).startsWith(dollar[1].toLowerCase())),
    };

  const path = before.match(/(?:^|[^$\w.`])([A-Za-z_$][\w$.]*)$/);
  if (path)
    return {
      start: caret - path[1].length,
      items: payloadFieldPaths(sample.body)
        .filter((candidate) => candidate.toLowerCase().startsWith(path[1].toLowerCase()))
        .map((candidate) => ({ label: candidate, insert: candidate, detail: "Representative request field" })),
    };

  return { start: caret, items: [] };
}
