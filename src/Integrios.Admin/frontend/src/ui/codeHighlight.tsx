import { cn } from "cn";
import type { ComponentProps, ReactNode } from "react";
import { parseJson } from "./json";

/// The languages the dashboard shows an Operator: the documents Integrios carries, and the requests
/// a guide hands over to be pasted into a terminal or an editor. `text` is everything with no
/// grammar this dashboard knows — a callback URL, a broker address — and is painted not at all,
/// because colour there would claim a structure the value does not have.
export type CodeLanguage = "json" | "http" | "shell" | "csharp" | "text";

/// Each tokenizer names its groups after the four code colours, so one painter serves them all.
/// Deliberately tokenizers and not parsers: a shown document may be a fragment, a half-typed editor
/// value, or a provider's own JSON, and a parser has nothing to say about any of those.
const tint = {
  key: "text-code-key",
  string: "text-code-string",
  literal: "text-code-literal",
  number: "text-code-number",
};

/// A quoted run followed by a colon is a property name, any other quoted run a string value, then
/// the bare literals and numbers. Everything unmatched — punctuation, whitespace — is left as text.
const json =
  /(?<key>"(?:\\.|[^"\\])*")(?=\s*:)|(?<string>"(?:\\.|[^"\\])*")|(?<literal>\b(?:true|false|null)\b)|(?<number>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g;

/// A request line's method, and the name of each header under it. Only the head of the message: a
/// body is JSON and is painted as JSON, so nothing here has to know what a body holds.
const httpHead = /^(?<literal>[A-Z]+)(?= \S)|^(?<key>[A-Za-z][\w-]*)(?=:)/gm;

/// A command as a shell reads it: the options that select behaviour, and the quoted arguments they
/// carry. A `--data` argument is JSON inside quotes and stays one string, because that is the one
/// thing the shell will hand over verbatim.
const shell = /(?<=^|\s)(?<key>--?[A-Za-z][\w-]*)|(?<string>'(?:[^'\\]|\\.)*'|"(?:\\.|[^"\\])*")/g;

/// Strings first, raw strings before ordinary ones, so a keyword inside quoted text stays text.
const csharp =
  /(?<string>"""[\s\S]*?"""|"(?:\\.|[^"\\])*")|(?<literal>\b(?:using|var|new|await|async|return|class|public|private|static|void|string|int|bool|true|false|null)\b)|(?<number>\b\d+\b)/g;

function paint(source: string, pattern: RegExp, prefix: string): ReactNode[] {
  const nodes: ReactNode[] = [];
  let last = 0;
  for (const match of source.matchAll(pattern)) {
    const start = match.index;
    if (start > last) nodes.push(source.slice(last, start));
    const group = Object.entries(match.groups ?? {}).find(([, value]) => value !== undefined)?.[0];
    nodes.push(
      <span key={`${prefix}${start}`} className={group === undefined ? undefined : tint[group as keyof typeof tint]}>
        {match[0]}
      </span>,
    );
    last = start + match[0].length;
  }
  nodes.push(source.slice(last));
  return nodes;
}

export function highlightCode(source: string, language: CodeLanguage = "json"): ReactNode[] {
  if (language === "text") return [source];
  if (language === "json") return paint(source, json, "");
  if (language === "shell") return paint(source, shell, "");
  if (language === "csharp") return paint(source, csharp, "");
  // An HTTP message is two documents: a head with its own grammar, and a body that is JSON.
  const separator = source.indexOf("\n\n");
  if (separator < 0) return paint(source, httpHead, "");
  return [
    ...paint(source.slice(0, separator + 2), httpHead, "head-"),
    ...paint(source.slice(separator + 2), json, "body-"),
  ];
}

/// Every document the dashboard shows is one kind of box, so a manifest, a stored body, and a
/// request to paste read alike on every screen. `className` is for where the box sits — height,
/// scroll — not for how the code inside it reads.
///
/// Colour is for the documents that have a grammar this dashboard knows. A value that is not JSON
/// and names no language — a callback URL, a JSONata expression — is shown unpainted, because
/// painting keywords into it would claim a structure it does not have.
export function CodeBlock({
  value,
  language,
  className,
}: {
  value: unknown;
  language?: CodeLanguage;
  className?: string;
}) {
  const text = typeof value === "string" ? value : (JSON.stringify(value, null, 2) ?? "");
  const painted = language ?? (typeof value !== "string" || parseJson(text).error === undefined ? "json" : "text");
  return (
    <pre
      className={cn(
        "m-0 min-w-0 rounded-md border bg-surface-quiet p-3 font-mono leading-normal break-words whitespace-pre-wrap",
        className,
      )}
    >
      {highlightCode(text, painted)}
    </pre>
  );
}

/// `CodeBlock`'s editable twin, and a drop-in for `Textarea`: every prop reaches the real textarea.
/// The document is painted once, highlighted, by a `<pre>` in normal flow; the textarea sits over
/// it with transparent glyphs and its own caret, so everything a textarea gives for free —
/// selection, undo, the accessible name its label carries — is still a textarea's, and nothing here
/// reimplements an editor.
///
/// The `<pre>` is what sizes the box, so the two never need their scroll positions kept in step:
/// they grow together and the frame around them does the scrolling. `className` sizes that frame.
export function CodeTextarea({
  language = "json",
  className,
  value,
  ...textarea
}: Omit<ComponentProps<"textarea">, "value" | "defaultValue"> & {
  /// Controlled only: the painted copy is drawn from this, so an uncontrolled textarea would type
  /// into a box that shows nothing.
  value: string;
  language?: CodeLanguage;
}) {
  return (
    <div
      className={cn(
        "relative min-h-32 min-w-0 overflow-auto rounded-md border border-input bg-transparent focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/50 has-[[aria-invalid=true]]:border-destructive",
        className,
      )}
    >
      <div className="relative min-h-full w-full">
        <pre
          aria-hidden="true"
          className="m-0 overflow-hidden px-3 py-2 font-mono leading-normal break-words whitespace-pre-wrap"
        >
          {/* A document ending in a newline has no line box for that last line unless something
              follows it, so the painted copy runs one line longer than the text it mirrors. */}
          {highlightCode(value, language)}
          {"\n"}
        </pre>
        <textarea
          spellCheck={false}
          autoComplete="off"
          {...textarea}
          value={value}
          className="absolute inset-0 size-full resize-none overflow-hidden bg-transparent px-3 py-2 font-mono leading-normal break-words whitespace-pre-wrap text-transparent caret-ink outline-none placeholder:text-muted-foreground disabled:cursor-not-allowed"
        />
      </div>
    </div>
  );
}
