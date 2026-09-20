import { CalendarDays, ChevronDown } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { filterPill } from "./fields";
import { instant, instantText, localInputValue } from "./time";

const presets = [
  { label: "Last hour", minutes: 60 },
  { label: "Last 24 h", minutes: 24 * 60 },
  { label: "Last 7 d", minutes: 7 * 24 * 60 },
];

/// The range in force, as the pill states it on the bar.
function summary(from: string, to: string): string {
  if (from && to) return `${instantText(from)} – ${instantText(to)}`;
  if (from) return `from ${instantText(from)}`;
  if (to) return `to ${instantText(to)}`;
  return "";
}

/// The one control for the accepted range: what the ledger is scoped to, in the viewer's zone, and
/// the way to change it. The state is the URL's two instants and nothing else, so a chart selection
/// lands here and a preset means the same range whenever its link is opened — presets resolve to
/// fixed instants at the moment they are picked, exactly as a chart selection does.
///
/// Only the Custom inputs pass through a local wall clock, and a bound the Operator did not edit
/// keeps its instant: in the repeated fall-back hour one wall-clock reading names two instants, and
/// re-reading it would silently move the range to the earlier one.
export function AcceptedRangePill({
  from,
  to,
  onChange,
}: {
  from: string;
  to: string;
  onChange: (from: string, to: string) => void;
}) {
  const [open, setOpen] = useState(false);
  // What is typed in the Custom inputs, as the local wall clock they hold. It is read into the
  // range only when the Operator applies it — Apply, or Enter — so a half-open range is never read
  // and leaving the panel discards a draft rather than scoping the ledger to it.
  const [draft, setDraft] = useState({ from: "", to: "" });

  // A bound the Operator did not edit keeps its instant; an unchanged range writes nothing, so
  // opening and closing the panel is not a history entry.
  const commitCustom = () => {
    const bound = (edited: string, original: string) =>
      edited === localInputValue(original) ? original : (instant(edited) ?? "");
    const [nextFrom, nextTo] = [bound(draft.from, from), bound(draft.to, to)];
    if (nextFrom !== from || nextTo !== to) onChange(nextFrom, nextTo);
    setOpen(false);
  };

  return (
    <Popover
      open={open}
      onOpenChange={(next) => {
        if (next) setDraft({ from: localInputValue(from), to: localInputValue(to) });
        setOpen(next);
      }}
    >
      <PopoverTrigger
        data-applied={String(from !== "" || to !== "")}
        // The visible reading drops the year; the exact instants stay reachable here.
        title={from || to ? `${from || "…"} – ${to || "…"}` : undefined}
        className={`${filterPill} cursor-pointer justify-start`}
      >
        <CalendarDays aria-hidden="true" className="size-4 shrink-0 text-ink-secondary" />
        <span className="font-normal text-ink-secondary">Accepted</span>
        {from || to ? <span className="min-w-0 truncate font-medium">{summary(from, to)}</span> : null}
        <ChevronDown aria-hidden="true" className="size-3 shrink-0 text-ink-secondary" />
      </PopoverTrigger>
      <PopoverContent aria-label="Accepted range">
        <div className="flex flex-wrap gap-2">
          {presets.map(({ label, minutes }) => (
            <Button
              key={label}
              type="button"
              variant="outline"
              size="sm"
              onClick={() => {
                const now = Date.now();
                onChange(new Date(now - minutes * 60_000).toISOString(), new Date(now).toISOString());
                setOpen(false);
              }}
            >
              {label}
            </Button>
          ))}
        </div>
        <fieldset className="m-0 flex flex-col gap-2 border-0 p-0">
          <legend className="mb-1 p-0 font-medium">Custom range</legend>
          {(["from", "to"] as const).map((end) => (
            <label key={end} className="flex flex-col gap-1">
              <span className="text-ink-secondary">{end === "from" ? "From" : "To"}</span>
              <input
                type="datetime-local"
                step="1"
                value={draft[end]}
                onChange={(event) => setDraft((current) => ({ ...current, [end]: event.target.value }))}
                onKeyDown={(event) => {
                  if (event.key !== "Enter") return;
                  event.preventDefault();
                  commitCustom();
                }}
                className="h-9 rounded-md border border-input bg-transparent px-2 text-base outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 md:text-sm"
              />
            </label>
          ))}
        </fieldset>
        {/* Both ends land together, so the range is applied by one control rather than by leaving. */}
        <div className="flex items-center justify-between gap-2">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={from === "" && to === ""}
            onClick={() => {
              onChange("", "");
              setOpen(false);
            }}
          >
            Clear
          </Button>
          <Button type="button" size="sm" onClick={commitCustom}>
            Apply
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  );
}
