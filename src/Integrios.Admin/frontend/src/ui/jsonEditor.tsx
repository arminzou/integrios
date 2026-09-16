import type { ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { formatJson, parseJson } from "./json";

/// One pass over a JSON document, in the order the grammar disambiguates: a quoted run followed by
/// a colon is a property name, any other quoted run is a string value, then the bare literals and
/// numbers. Everything unmatched — punctuation, whitespace — is left as text.
///
/// Deliberately a tokenizer and not a parser: the editor's whole job is to stay legible while the
/// document is half-typed and invalid, which is exactly when a parser has nothing to say.
const token = /("(?:\\.|[^"\\])*")(\s*:)|("(?:\\.|[^"\\])*")|\b(true|false|null)\b|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g;

function highlight(source: string): ReactNode[] {
  const nodes: ReactNode[] = [];
  let last = 0;
  for (const match of source.matchAll(token)) {
    const start = match.index;
    if (start > last) nodes.push(source.slice(last, start));
    const [text, key, colon, string, literal] = match;
    const className = key
      ? "text-code-key"
      : string
        ? "text-code-string"
        : literal
          ? "text-code-literal"
          : "text-code-number";
    nodes.push(
      <span key={start} className={className}>
        {key ?? text}
      </span>,
    );
    if (colon) nodes.push(colon);
    last = start + text.length;
  }
  nodes.push(source.slice(last));
  return nodes;
}

/// A textarea that reads like code. The document is painted once, highlighted, by a `<pre>` in
/// normal flow; the real textarea sits over it with transparent glyphs and its own caret, so
/// everything a textarea gives for free — selection, undo, spellcheck off, the accessible name its
/// label carries — is still a textarea's, and nothing here reimplements an editor.
///
/// The `<pre>` is what sizes the box, so the two never need their scroll positions kept in step:
/// they grow together and the surface around them does the scrolling.
export function JsonEditor({
  id,
  label,
  value,
  invalid,
  onChange,
}: {
  id: string;
  label: string;
  value: string;
  invalid: boolean;
  onChange: (value: string) => void;
}) {
  const parsed = parseJson(value);
  const formatted = parsed.error === undefined ? formatJson(parsed.value) : null;

  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <div className="flex items-center justify-between gap-2">
        <label htmlFor={id} className="text-sm font-medium">
          {label}
        </label>
        {/* Nothing to do when the document is already what formatting would produce, and nothing it
            can do when the document does not parse. */}
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={formatted === null || formatted === value}
          onClick={() => formatted !== null && onChange(formatted)}
        >
          Format
        </Button>
      </div>
      <div
        className={`relative min-w-0 rounded-md border bg-transparent focus-within:ring-[3px] focus-within:ring-ring/50 ${
          invalid ? "border-destructive focus-within:border-destructive" : "border-input focus-within:border-ring"
        }`}
      >
        <pre
          aria-hidden="true"
          className="m-0 min-h-120 overflow-hidden px-3 py-2 font-mono text-sm break-words whitespace-pre-wrap"
        >
          {/* A document ending in a newline has no line box for that last line unless something
              follows it, so the painted copy runs one line longer than the text it mirrors. */}
          {highlight(value)}
          {"\n"}
        </pre>
        <textarea
          id={id}
          spellCheck={false}
          autoComplete="off"
          aria-invalid={invalid}
          value={value}
          onChange={(event) => onChange(event.target.value)}
          className="absolute inset-0 size-full resize-none overflow-hidden bg-transparent px-3 py-2 font-mono text-sm break-words whitespace-pre-wrap text-transparent caret-ink outline-none"
        />
      </div>
    </div>
  );
}
