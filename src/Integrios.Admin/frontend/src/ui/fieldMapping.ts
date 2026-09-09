export type FieldMapping = { output: string; source: string };

export function parseFieldMappings(expression: string): FieldMapping[] | undefined {
  const trimmed = expression.trim();
  if (trimmed === "") return [];
  if (!trimmed.startsWith("{") || !trimmed.endsWith("}")) return undefined;

  const body = trimmed.slice(1, -1);
  if (body.trim() === "") return [];

  const rows: FieldMapping[] = [];
  let remaining = body;
  const fieldName = "(?:[A-Za-z_$][\\w$]*|`(?:\\\\.|[^`\\\\])+`)";
  const entry = new RegExp(
    `^\\s*("(?:\\\\.|[^"\\\\])*")\\s*:\\s*((?:\\$context|(?:\\$\\.)?${fieldName})(?:\\.${fieldName}|\\[\\d+\\])*)\\s*(,|$)`,
  );
  while (remaining.length > 0) {
    const match = remaining.match(entry);
    if (!match) return undefined;
    let output: string;
    try {
      output = JSON.parse(match[1]);
    } catch {
      return undefined;
    }
    if (rows.some((row) => row.output === output)) return undefined;
    rows.push({ output, source: match[2].replace(/^\$\./, "") });
    remaining = remaining.slice(match[0].length);
    if (match[3] === "" && remaining.trim() !== "") return undefined;
    if (match[3] === "," && remaining.trim() === "") return undefined;
  }
  return rows;
}

export function expressionFromFieldMappings(rows: FieldMapping[]): string {
  const complete = rows.filter((row) => row.output.trim() && row.source);
  if (complete.length === 0) return "";
  return `{
${complete.map((row) => `  ${JSON.stringify(row.output.trim())}: ${row.source}`).join(",\n")}
}`;
}

export function payloadPlaceholder(rows: FieldMapping[]): Record<string, unknown> {
  const blank = () => Object.create(null) as Record<string, unknown>;
  const root = blank();
  for (const { source } of rows) {
    if (source.startsWith("$context")) continue;
    const parts = [...source.matchAll(/`((?:\\.|[^`\\])+)`|([A-Za-z_$][\w$]*)|\[(\d+)\]/g)].map(
      ([, quoted, field, index]) =>
        index === undefined ? (quoted ?? field).replace(/\\([\\`])/g, "$1") : Number(index),
    );
    if (parts.some((part) => typeof part === "number" && part > 99)) continue;
    let current: Record<string | number, unknown> | unknown[] = root;
    parts.forEach((part, index) => {
      const container = current as Record<string | number, unknown>;
      if (index === parts.length - 1) {
        container[part] = null;
        return;
      }
      const next = typeof parts[index + 1] === "number" ? [] : blank();
      if (container[part] === null || typeof container[part] !== "object") container[part] = next;
      current = container[part] as Record<string | number, unknown> | unknown[];
    });
  }
  return JSON.parse(JSON.stringify(root)) as Record<string, unknown>;
}

export function payloadFieldPaths(value: unknown, prefix = ""): string[] {
  if (value === null || typeof value !== "object") return prefix ? [prefix] : [];
  if (Array.isArray(value)) return prefix ? [prefix] : [];

  return Object.entries(value).flatMap(([key, child]) => {
    const field = /^[A-Za-z_$][\w$]*$/.test(key) ? key : `\`${key.replaceAll("\\", "\\\\").replaceAll("`", "\\`")}\``;
    const path = prefix ? `${prefix}.${field}` : field;
    return child !== null && typeof child === "object" && !Array.isArray(child)
      ? payloadFieldPaths(child, path)
      : [path];
  });
}
