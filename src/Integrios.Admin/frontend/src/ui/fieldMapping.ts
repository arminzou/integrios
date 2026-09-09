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
