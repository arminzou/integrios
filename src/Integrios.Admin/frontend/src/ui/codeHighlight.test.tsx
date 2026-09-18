import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { type CodeLanguage, highlightCode } from "./codeHighlight";

afterEach(cleanup);

/// What colour each painted run carries, read back off the rendered document. The unpainted text
/// between them is not asserted: `textContent` covering the source is what says nothing was lost.
function painted(source: string, language: CodeLanguage) {
  const { container } = render(<pre>{highlightCode(source, language)}</pre>);
  expect(container.textContent).toBe(source);
  return [...container.querySelectorAll("span")].map((span) => [span.className, span.textContent]);
}

describe("Highlighting a shown document", () => {
  it("colours a JSON document by its grammar", () => {
    expect(painted('{"id": "SO-1", "n": 2, "ok": true}', "json")).toEqual([
      ["text-code-key", '"id"'],
      ["text-code-string", '"SO-1"'],
      ["text-code-key", '"n"'],
      ["text-code-number", "2"],
      ["text-code-key", '"ok"'],
      ["text-code-literal", "true"],
    ]);
  });

  it("colours an HTTP message's head as headers and its body as JSON", () => {
    expect(
      painted('POST https://x.test/events?source_id=7\nContent-Type: application/json\n\n{"n": 1}', "http"),
    ).toEqual([
      ["text-code-literal", "POST"],
      ["text-code-key", "Content-Type"],
      ["text-code-key", '"n"'],
      ["text-code-number", "1"],
    ]);
  });

  it("colours a command's options and leaves each quoted argument whole", () => {
    expect(painted(`curl --request POST 'https://x.test' --data '{"n": 1}'`, "shell")).toEqual([
      ["text-code-key", "--request"],
      ["text-code-string", "'https://x.test'"],
      ["text-code-key", "--data"],
      ["text-code-string", `'{"n": 1}'`],
    ]);
  });

  it("colours C# keywords and keeps a raw string one run", () => {
    expect(painted('using var client = new HttpClient("""\nvar not-code\n""");', "csharp")).toEqual([
      ["text-code-literal", "using"],
      ["text-code-literal", "var"],
      ["text-code-literal", "new"],
      ["text-code-string", '"""\nvar not-code\n"""'],
    ]);
  });

  it("paints nothing without a grammar to paint by", () => {
    expect(painted("https://x.test/webhooks/9ad1", "text")).toEqual([]);
  });
});

/// A screen that renders its own `<pre>` shows a document in a box of its own making, unpainted —
/// which is how five panels drifted apart before this rule existed.
describe("Showing a document on a screen", () => {
  const folder = join(process.cwd(), "src/screens");
  const screens = readdirSync(folder)
    .filter((name) => name.endsWith(".tsx") && !name.endsWith(".test.tsx"))
    .map((name) => [name, readFileSync(join(folder, name), "utf8")] as const);

  it("goes through CodeBlock rather than a bare pre", () => {
    const offenders = screens.filter(([, source]) => /<pre[\s>]/.test(source)).map(([name]) => name);
    expect(screens.length).toBeGreaterThan(0);
    expect(offenders).toEqual([]);
  });

  it("edits a document in CodeTextarea rather than a textarea styled as code", () => {
    const offenders = screens
      .filter(
        ([, source]) =>
          source.includes("@/components/ui/textarea") || /<TextAreaField(?:(?!\/>)[\s\S])*font-mono/.test(source),
      )
      .map(([name]) => name);
    expect(offenders).toEqual([]);
  });
});
