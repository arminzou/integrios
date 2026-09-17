/// What a pasted `curl` command says about the request it sends: its headers as written, and its
/// body. Everything else a command carries — method, URL, TLS and output flags — is ignored, because
/// the sample is only the request a Source would receive.
export type CurlRequest = { headers: { name: string; value: string }[]; body: string | null };

const headerFlags = new Set(["-H", "--header"]);
const dataFlags = new Set(["-d", "--data", "--data-raw", "--data-binary"]);

const ansiEscapes: Record<string, string> = {
  a: "\x07",
  b: "\b",
  e: "\x1b",
  E: "\x1b",
  f: "\f",
  n: "\n",
  r: "\r",
  t: "\t",
  v: "\v",
  "\\": "\\",
  "'": "'",
  '"': '"',
  "?": "?",
};

/// Splits a command into words the way a POSIX shell quotes them, including bash's `$'…'`, which
/// browsers and request bins emit whenever a body holds a newline or an apostrophe.
function words(text: string): string[] {
  const result: string[] = [];
  let word = "";
  let inWord = false;
  let at = 0;
  const hex = (length: number) => {
    const digits = text.slice(at, at + length).match(/^[0-9a-fA-F]+/)?.[0] ?? "";
    at += digits.length;
    return digits;
  };
  while (at < text.length) {
    const char = text[at];
    if (char === "\\" && text[at + 1] === "\n") {
      at += 2;
    } else if (char === "\\" && text.startsWith("\r\n", at + 1)) {
      at += 3;
    } else if (/\s/.test(char)) {
      if (inWord) result.push(word);
      word = "";
      inWord = false;
      at++;
    } else if (char === "'") {
      const end = text.indexOf("'", at + 1);
      if (end < 0) throw new Error("The command has an unclosed ' quote.");
      word += text.slice(at + 1, end);
      inWord = true;
      at = end + 1;
    } else if (char === "$" && text[at + 1] === "'") {
      at += 2;
      inWord = true;
      for (;;) {
        if (at >= text.length) throw new Error("The command has an unclosed $' quote.");
        const next = text[at++];
        if (next === "'") break;
        if (next !== "\\") {
          word += next;
          continue;
        }
        const code = text[at++];
        if (code === "x") {
          const digits = hex(2);
          word += digits ? String.fromCharCode(Number.parseInt(digits, 16)) : "\\x";
        } else if (code === "u" || code === "U") {
          const digits = hex(code === "u" ? 4 : 8);
          word += digits ? String.fromCodePoint(Number.parseInt(digits, 16)) : `\\${code}`;
        } else if (code !== undefined && code in ansiEscapes) {
          word += ansiEscapes[code];
        } else {
          word += `\\${code ?? ""}`;
        }
      }
    } else if (char === '"') {
      at++;
      inWord = true;
      for (;;) {
        if (at >= text.length) throw new Error('The command has an unclosed " quote.');
        const next = text[at++];
        if (next === '"') break;
        if (next === "\\" && text[at] === "\n") at++;
        else if (next === "\\" && '$`"\\'.includes(text[at])) word += text[at++];
        else word += next;
      }
    } else if (char === "\\") {
      word += text[at + 1] ?? "";
      inWord = true;
      at += 2;
    } else {
      word += char;
      inWord = true;
      at++;
    }
  }
  if (inWord) result.push(word);
  return result;
}

/// Reads the headers and body out of a `curl` command. Throws the message an Operator has to act
/// on when the paste cannot be read at all.
export function parseCurl(text: string): CurlRequest {
  const args = words(text);
  const headers: CurlRequest["headers"] = [];
  const data: string[] = [];
  for (let at = 0; at < args.length; at++) {
    const arg = args[at];
    let header: string | undefined;
    if (headerFlags.has(arg)) header = args[++at];
    else if (arg.startsWith("-H") && arg.length > 2) header = arg.slice(2);
    else if (dataFlags.has(arg)) {
      if (at + 1 < args.length) data.push(args[++at]);
    } else if (arg.startsWith("-d") && arg.length > 2) data.push(arg.slice(2));
    if (header === undefined) continue;
    const colon = header.indexOf(":");
    if (colon <= 0) continue;
    headers.push({ name: header.slice(0, colon), value: header.slice(colon + 1).replace(/^[ \t]+/, "") });
  }
  // curl sends repeated data options as one body joined with `&`.
  return { headers, body: data.length > 0 ? data.join("&") : null };
}
