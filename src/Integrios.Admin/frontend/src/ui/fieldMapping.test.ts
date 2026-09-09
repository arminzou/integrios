import { describe, expect, it } from "vitest";
import { expressionFromFieldMappings, parseFieldMappings, payloadFieldPaths, payloadPlaceholder } from "./fieldMapping";

describe("field mappings", () => {
  it("round-trips the simple JSONata subset without accepting advanced expressions", () => {
    const rows = parseFieldMappings('{ "order": $.orderId, "type": $context.event_type }');

    expect(rows).toEqual([
      { output: "order", source: "orderId" },
      { output: "type", source: "$context.event_type" },
    ]);
    expect(expressionFromFieldMappings(rows ?? [])).toBe('{\n  "order": orderId,\n  "type": $context.event_type\n}');
    expect(parseFieldMappings("items.$map(function($item) { $item.id })")).toBeUndefined();
  });

  it("lists nested payload fields and quotes JSONata-unsafe names", () => {
    expect(payloadFieldPaths({ orderId: "1", customer: { id: "2" }, "placed-at": "today", items: [] })).toEqual([
      "orderId",
      "customer.id",
      "`placed-at`",
      "items",
    ]);
  });

  it("builds null payload placeholders and excludes Event context", () => {
    expect(
      payloadPlaceholder([
        { output: "customer", source: "customer.id" },
        { output: "line", source: "items[0].sku" },
        { output: "placed", source: "`placed-at`" },
        { output: "type", source: "$context.event_type" },
      ]),
    ).toEqual({ customer: { id: null }, items: [{ sku: null }], "placed-at": null });
    const hostile = payloadPlaceholder([
      { output: "pollute", source: "__proto__.polluted" },
      { output: "huge", source: "items[999999999].sku" },
    ]);
    expect(hostile).toEqual(JSON.parse('{"__proto__":{"polluted":null}}'));
    expect(({} as { polluted?: unknown }).polluted).toBeUndefined();
  });
});
