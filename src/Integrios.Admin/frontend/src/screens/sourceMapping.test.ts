import { describe, expect, it } from "vitest";
import { type EventTypeRule, emptyEventType, guidedExpression, guidedFrom, headerContext } from "./sourceMapping";

const webhook: EventTypeRule = { source: "header", header: "x-github-event", prefix: "github" };

describe("The generated Source mapping", () => {
  it("reads the Event type from the bounded webhook context and refuses a request missing it", () => {
    const expression = guidedExpression(webhook);

    expect(expression).toContain("$event := $context.headers.`x-github-event`");
    expect(expression).toContain('"event_type": "github." & $event');
    expect(expression).toContain('$exists($event) and $type($event) = "string" and $event != ""');
    expect(expression).toContain("$error(");
  });

  it("reads an Event type directly from a body field and enforces a non-empty string", () => {
    const expression = guidedExpression({ source: "body", path: "event.type", prefix: "" });

    expect(expression).toContain("$event := event.type");
    expect(expression).toContain('"event_type": $event');
    expect(expression).toContain('$type($event) = "string"');
  });

  /// Event identity is the Source's own rule, never a mapped field. A guided mapping that
  /// emitted one gave the Source two identities with different permanence.
  it("never emits an Event identity, whatever was chosen", () => {
    expect(guidedExpression(webhook)).not.toContain("source_event_id");
    expect(guidedExpression(emptyEventType)).not.toContain("source_event_id");
  });

  /// Shaping the payload belongs to the Subscription that knows the destination. A Source that
  /// trimmed it would trim it for every Subscription on the Topic, and for the Event ledger.
  it("always carries the whole input as the payload", () => {
    expect(guidedExpression(webhook)).toContain('"payload": $');
    expect(guidedExpression({ source: "body", path: "event.type", prefix: "" })).toContain('"payload": $');
    expect(guidedExpression({ source: "fixed", value: "queue.order" })).toBe(
      '{ "event_type": "queue.order", "payload": $ }',
    );
  });

  it("leaves out the checks a fixed Event type does not need", () => {
    const expression = guidedExpression({ source: "fixed", value: "queue.order" });

    expect(expression).not.toContain("$error(");
    expect(expression).not.toContain("$event");
  });
});

describe("Reading a mapping back into its rule", () => {
  it("round-trips every rule the guided form can author", () => {
    const rules: EventTypeRule[] = [
      emptyEventType,
      { source: "fixed", value: "order.placed" },
      webhook,
      { source: "header", header: "x-github-event", prefix: "" },
      { source: "body", path: "event.type", prefix: "acme" },
      { source: "body", path: "data.`odd-key`.kind", prefix: "" },
    ];

    for (const rule of rules) expect(guidedFrom(guidedExpression(rule))).toEqual(rule);
  });

  /// The round trip is what lets the guided pane claim an expression. An expression it did not
  /// write must be refused, or reopening a Source would silently rewrite a contract the Operator
  /// authored by hand.
  it("refuses an expression the guided form would not have written", () => {
    expect(guidedFrom("")).toBeUndefined();
    expect(guidedFrom('{ "event_type": "a", "payload": payload.inner }')).toBeUndefined();
    // Shaped like the generated form but not equal to it: the prefix literal carries no separator,
    // so no rule produces this text and the guided pane must not claim one does.
    expect(
      guidedFrom(
        '($event := $context.headers.`x-github-event`; $exists($event) and $type($event) = "string" and $event != "" ? ' +
          '{ "event_type": "github" & $event, "payload": $ } : $error("x"))',
      ),
    ).toBeUndefined();
    expect(guidedFrom('{ "event_type": "a", "payload": $, "source_event_id": id }')).toBeUndefined();
    expect(guidedFrom('($x := 1; { "event_type": "a", "payload": $ })')).toBeUndefined();
  });

  /// The payload-shaping mappings this form used to generate are exactly the contracts it may no
  /// longer claim: they stay readable as raw JSONata rather than being rewritten to the whole body.
  it("refuses a mapping that shapes the payload per field", () => {
    expect(
      guidedFrom('{ "event_type": "queue.order", "payload": { "repository": repository.full_name } }'),
    ).toBeUndefined();
    expect(
      guidedFrom(
        '($event := $context.headers.`x-github-event`; $exists($event) and $type($event) = "string" and $event != "" ? ' +
          '{ "event_type": "github." & $event, "payload": { "id": number } } : $error("x"))',
      ),
    ).toBeUndefined();
  });
});

describe("Sample request headers", () => {
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
