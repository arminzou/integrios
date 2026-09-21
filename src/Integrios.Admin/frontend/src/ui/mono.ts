/// A monospace field states its size through `--mono-size` rather than a fixed one, so it follows the
/// same rule as every other monospace run: 13px, or 12px inside an Inspector. Both sizes are given
/// because the Input primitive sets its own at `md`, and would otherwise win there.
export const monoInput = "font-mono text-(length:--mono-size) md:text-(length:--mono-size)";
