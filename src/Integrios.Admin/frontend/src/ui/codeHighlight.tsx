import type { ReactNode } from "react";

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
