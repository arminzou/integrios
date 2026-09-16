/// The guided half of the Integrios Event Builder: the one mapping choice an Operator makes about a
/// provider-native request, from which the persisted artefact — a JSONata expression in the existing
/// versioned envelope — is generated. Nothing here is stored: the expression is.
///
/// The payload is always the whole input. Shaping it per field belongs to the Subscription's Mapping
/// Playground, which is the surface that knows the destination; a Source that trimmed the payload
/// would take that choice away from every Subscription on the Topic, and from the Event ledger,
/// irreversibly and at the moment there is least reason to.
///
/// Event identity is not generated here either. The Source's own Event-identity rule owns it and is
/// read before this mapping runs; a second identity emitted by the expression meant one concept
/// authored twice, on two surfaces, with different permanence.
export type EventTypeRule =
  | { source: "fixed"; value: string }
  | { source: "header"; header: string; prefix: string }
  | { source: "body"; path: string; prefix: string };

export const emptyEventType: EventTypeRule = { source: "fixed", value: "" };

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

/// The sample headers, as the runtime would present them. Throws the message an Operator has
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

const missingEventType = "This request does not contain a valid Event type.";

/// Generates the expression the runtime evaluator actually runs. The rule is never persisted beside
/// it — this text is the whole contract — so `guidedFrom` reads the rule back out of the expression
/// rather than a stored copy that could disagree with it.
export function guidedExpression(rule: EventTypeRule): string {
  if (rule.source === "fixed") return `{ "event_type": ${JSON.stringify(rule.value.trim())}, "payload": $ }`;

  const selector = rule.source === "header" ? headerReference(rule.header) : rule.path;
  const prefix = rule.prefix.trim();
  const eventType = prefix ? `${JSON.stringify(`${prefix}.`)} & $event` : "$event";
  // The type check is not redundant beside the existence one: a JSON number or object at the chosen
  // path exists, and would be concatenated into an event_type no Subscription could ever match.
  return (
    `($event := ${selector}; $exists($event) and $type($event) = "string" and $event != "" ? ` +
    `{ "event_type": ${eventType}, "payload": $ } : $error(${JSON.stringify(missingEventType)}))`
  );
}

const jsonString = String.raw`"(?:\\.|[^"\\])*"`;
const fixedShape = new RegExp(String.raw`^\{ "event_type": (${jsonString}), "payload": \$ \}$`);
const derivedShape = new RegExp(
  String.raw`^\(\$event := (.+); \$exists\(\$event\) and \$type\(\$event\) = "string" and \$event != "" \? ` +
    String.raw`\{ "event_type": (.+), "payload": \$ \} : \$error\(${jsonString}\)\)$`,
);
const headerShape = /^\$context\.headers\.`([^`]+)`$/;
const prefixedShape = new RegExp(String.raw`^(${jsonString}) & \$event$`);

/// Reads an expression back into the rule that would generate it, or nothing when no rule would.
/// The inverse exists so a Source authored here reopens in the form that authored it: without it the
/// guided pane cannot claim to represent its own output, and an Operator returning to a Source is
/// told to reset an expression the guided form wrote minutes earlier.
export function guidedFrom(expression: string): EventTypeRule | undefined {
  const text = expression.trim();
  const rule = proposed(text);
  // The patterns only have to propose a rule; regenerating is what proves it. Anything that does not
  // produce this exact expression is not what wrote it, whatever it parsed as, and the guided pane
  // must not claim to represent it.
  return rule && guidedExpression(rule) === text ? rule : undefined;
}

function proposed(text: string): EventTypeRule | undefined {
  const fixed = text.match(fixedShape);
  if (fixed) return { source: "fixed", value: JSON.parse(fixed[1]) as string };

  const derived = text.match(derivedShape);
  if (!derived) return undefined;

  const [, selector, eventType] = derived;
  const prefixed = eventType.match(prefixedShape);
  if (!prefixed && eventType !== "$event") return undefined;
  // The generated form is `"prefix." & $event`, so the literal carries a trailing separator the rule
  // itself does not. A literal without one regenerates differently and is refused above.
  const prefix = prefixed ? (JSON.parse(prefixed[1]) as string).slice(0, -1) : "";

  const header = selector.match(headerShape);
  return header ? { source: "header", header: header[1], prefix } : { source: "body", path: selector, prefix };
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
