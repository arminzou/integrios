import axe from "axe-core";
import { expect } from "vitest";

/// Rules jsdom cannot decide. Contrast and target size need real layout and computed colour, which
/// this environment does not produce; they belong to the human review rather than to a rule that
/// would pass here for the wrong reason.
const withoutLayout = ["color-contrast", "target-size"];

/// Runs the accessibility rules that can be decided from the rendered markup: every control has an
/// accessible name, errors are associated with their field, headings are ordered, tables declare
/// their headers, and ARIA is used correctly.
export async function expectNoAccessibilityViolations(container: HTMLElement) {
  const results = await axe.run(container, {
    rules: Object.fromEntries(withoutLayout.map((rule) => [rule, { enabled: false }])),
  });

  const violations = results.violations.map(
    (violation) =>
      `${violation.id} (${violation.impact}): ${violation.help}\n` +
      violation.nodes.map((node) => `  ${node.html}`).join("\n"),
  );

  expect(violations, violations.join("\n\n")).toEqual([]);
}
