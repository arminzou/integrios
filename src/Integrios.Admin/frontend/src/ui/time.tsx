/// Instants arrive from the Admin API as ISO 8601 strings. An ISO string is exact and close to
/// unreadable at a glance, and an Operator correlating the dashboard against a log or a graph is
/// reading a local clock, so the rendered value is local. The exact instant the API sent is never
/// lost to that formatting: it stays on `dateTime`, where it is machine-readable and copyable, and
/// the title carries it in full beside how long ago it was.
///
/// The visible value is absolute rather than relative on purpose. A ledger is scanned for ordering
/// and correlated against other systems, and "4 minutes ago" is a value that silently changes under
/// a screen left open; the elapsed reading is the secondary cue, on the title.
///
/// `undefined` as the locale is the Operator's own, resolved by the platform. No locale data is
/// bundled and no remote asset is loaded.
/// The year is dropped, not the seconds. Everything an Operator reads here is recent, so the year
/// is the one part that is never the answer to a question, while seconds are what separates two
/// delivery attempts in the same retry cycle. The full instant, year included, stays on the title.
const absolute = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
  second: "2-digit",
});

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

/// Largest unit first: the first one the elapsed time actually fills is the one worth naming.
const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 365 * 24 * 60 * 60_000],
  ["month", 30 * 24 * 60 * 60_000],
  ["day", 24 * 60 * 60_000],
  ["hour", 60 * 60_000],
  ["minute", 60_000],
  ["second", 1000],
];

export function since(value: string, now = Date.now()): string {
  const elapsed = new Date(value).getTime() - now;
  for (const [unit, milliseconds] of units)
    if (Math.abs(elapsed) >= milliseconds) return relative.format(Math.round(elapsed / milliseconds), unit);
  return relative.format(0, "second");
}

/// Formats one instant, or renders the value as sent when it cannot be parsed — an Operator quoting
/// a malformed value in a bug report needs to see what the API actually returned, not "Invalid Date".
export function Timestamp({ value }: { value: string }) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return <span className="font-mono text-sm">{value}</span>;

  return (
    <time dateTime={value} title={`${value}\n${since(value)}`} className="whitespace-nowrap tabular-nums">
      {absolute.format(date)}
    </time>
  );
}
