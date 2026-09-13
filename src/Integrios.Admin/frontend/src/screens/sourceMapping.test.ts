import { describe, expect, it } from "vitest";
import {
  completionsAt,
  duplicatePayloadFields,
  emptyGuided,
  guidedExpression,
  headerContext,
  matchesRequirementType,
  requirableFields,
  requirementsSchema,
} from "./sourceMapping";

const webhook = {
  ...emptyGuided,
  eventPrefix: "github",
  eventHeader: "x-github-event",
  actionPath: "action",
  identityHeader: "x-github-delivery",
  requireIdentity: true,
};

const body = { action: "opened", number: 4, draft: false, repository: { full_name: "northwind/orders" } };

describe("The generated Source mapping", () => {
  it("reads identity from the bounded webhook context and refuses a request missing it", () => {
    const expression = guidedExpression(webhook);

    expect(expression).toContain("$event := $context.headers.`x-github-event`");
    expect(expression).toContain("$delivery := $context.headers.`x-github-delivery`");
    expect(expression).toContain('"event_type": "github." & $event');
    expect(expression).toContain('"source_event_id": $delivery');
    expect(expression).toContain('"payload": $');
    // Both mapped identities were marked required, so neither reaches the Event as an empty string.
    expect(expression).toContain('$exists($event) and $event != "" and $exists($delivery) and $delivery != ""');
    expect(expression).toContain("$error(");
  });

  it("leaves out the checks and the envelope fields that were not chosen", () => {
    const expression = guidedExpression({ ...emptyGuided, eventPrefix: "queue.order" });

    expect(expression).toBe('{ "event_type": "queue.order", "payload": $ }');
    expect(expression).not.toContain("$error(");
    expect(expression).not.toContain("source_event_id");
  });

  it("stops rejecting the request when the identity is no longer required", () => {
    expect(guidedExpression({ ...webhook, requireIdentity: false })).not.toContain("$exists($delivery)");
  });

  it("names payload fields explicitly when the whole body is not the payload", () => {
    const expression = guidedExpression({
      ...webhook,
      payloadMode: "fields",
      payloadRows: [
        { output: "repository", source: "repository.full_name" },
        { output: "", source: "action" },
      ],
    });

    // A row with no output name is not yet a field, and is left out rather than guessed at.
    expect(expression).toContain('"payload": { "repository": repository.full_name }');
  });
});

describe("Representative request headers", () => {
  it("lower-cases names the way the runtime does and refuses a name it cannot resolve", () => {
    expect(headerContext([{ name: "X-GitHub-Event", value: "issues" }])).toEqual({ "x-github-event": "issues" });
    // A backtick-quoted name runs to the next backtick, so a name carrying one cannot be addressed.
    expect(() => headerContext([{ name: "x-a`b", value: "1" }])).toThrow(/backtick/);
    expect(() => headerContext([{ name: " ", value: "x" }])).toThrow(/name for every header/);
    expect(() =>
      headerContext([
        { name: "x-a", value: "1" },
        { name: "X-A", value: "2" },
      ]),
    ).toThrow(/more than once/);
  });
});

describe("Payload fields", () => {
  it("reports a name used twice, which JSONata refuses to evaluate at all", () => {
    expect(
      duplicatePayloadFields([
        { output: "id", source: "action" },
        { output: "id", source: "number" },
        { output: "other", source: "action" },
        { output: "", source: "action" },
      ]),
    ).toEqual(["id"]);
    expect(duplicatePayloadFields([{ output: "id", source: "action" }])).toEqual([]);
  });
});

describe("Input requirements", () => {
  it("offers only fields the manifest's flat input schema can express", () => {
    // The Admin API rejects nested object schemas and has no array type, so a nested value is not
    // offered as a requirement at all rather than authored into a manifest that must be refused.
    expect(requirableFields(body)).toEqual(["action", "number", "draft"]);
    expect(requirableFields([1, 2])).toEqual([]);
  });

  it("enforces presence and the authored type, and disappears entirely when the last row goes", () => {
    expect(
      requirementsSchema([
        { field: "action", type: "string" },
        { field: "number", type: "integer" },
        { field: "draft", type: "" },
      ]),
    ).toEqual({
      type: "object",
      properties: { action: { type: "string" }, number: { type: "integer" } },
      required: ["action", "number"],
      additionalProperties: true,
    });
    expect(requirementsSchema([])).toBeUndefined();
    expect(requirementsSchema([{ field: "", type: "" }])).toBeUndefined();
  });

  it("separates integer from number rather than accepting either for both", () => {
    expect(matchesRequirementType(4, "integer")).toBe(true);
    expect(matchesRequirementType(4.5, "integer")).toBe(false);
    expect(matchesRequirementType(4.5, "number")).toBe(true);
    expect(matchesRequirementType("4", "number")).toBe(false);
    expect(matchesRequirementType(false, "boolean")).toBe(true);
  });
});

describe("Advanced JSONata completion", () => {
  const sample = { headers: { "x-github-event": "issues" }, body };

  it("suggests the representative request's own headers, quoted the way the expression needs", () => {
    const text = "$context.headers.x-git";
    const { items, start } = completionsAt(text, text.length, sample);

    expect(items).toEqual([{ label: "`x-github-event`", insert: "`x-github-event`", detail: "issues" }]);
    expect(start).toBe("$context.headers.".length);
  });

  it("offers only functions the runtime evaluator has", () => {
    const { items } = completionsAt("$ex", 3, sample);
    expect(items.map((item) => item.label)).toEqual(["$exists()"]);
    // A function the evaluator does not register is never suggested.
    expect(completionsAt("$map", 4, sample).items).toEqual([]);
  });

  it("suggests paths the representative body really has", () => {
    const { items } = completionsAt("$exists(repo", 12, sample);
    expect(items.map((item) => item.insert)).toEqual(["repository.full_name"]);
    expect(completionsAt("nothing_here", 12, sample).items).toEqual([]);
  });
});
