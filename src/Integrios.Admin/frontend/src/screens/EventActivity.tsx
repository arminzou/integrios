import { useQuery } from "@tanstack/react-query";
import { cn } from "cn";
import { type KeyboardEvent, useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import { asProblem, call } from "../api/query";
import type { components } from "../api/schema";
import { ReadError } from "../ui/controls";

type EventActivityBucket = components["schemas"]["EventActivityBucketDto"];

export const activityRanges = [
  { value: "1h", label: "1 h" },
  { value: "24h", label: "24 h" },
  { value: "7d", label: "7 d" },
] as const;
export type ActivityRange = (typeof activityRanges)[number]["value"];

/// The four mutually exclusive outcomes every accepted Event is counted under, in stacking order
/// from the baseline. Both the Events chart and the Tenant overview name them exactly like this.
export const activityOutcomes = [
  { key: "routed", label: "Routed", swatch: "bg-success-ink/60" },
  { key: "awaiting_routing", label: "Awaiting routing", swatch: "bg-accent-border" },
  { key: "unrouted", label: "Unrouted", swatch: "bg-warning-ink/70" },
  { key: "delivery_dead_lettered", label: "Delivery dead-lettered", swatch: "bg-danger-ink" },
] as const;

export function useEventActivity(tenantId: string, range: ActivityRange) {
  return useQuery({
    queryKey: ["event-activity", tenantId, range],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/events/activity", {
          params: { path: { tenantId }, query: { range } },
        }),
      ),
  });
}

export function outcomeTotals(buckets: EventActivityBucket[]) {
  const totals = { routed: 0, awaiting_routing: 0, unrouted: 0, delivery_dead_lettered: 0 };
  for (const bucket of buckets) for (const { key } of activityOutcomes) totals[key] += Number(bucket[key]);
  return totals;
}

const bucketTime = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
});

function bucketName(bucket: EventActivityBucket): string {
  const counts = activityOutcomes.map(({ key, label }) => `${bucket[key]} ${label.toLowerCase()}`).join(", ");
  return `${bucketTime.format(new Date(bucket.start))} to ${bucketTime.format(new Date(bucket.end))}: ${counts}`;
}

/// Flow over time, and the only windowed element on the Events screen. Selecting intervals scopes the
/// ledger by acceptance time through the same URL filters an Operator could type, so the chart never
/// applies a window the filter bar does not show.
export function EventActivity({
  tenantId,
  selectedFrom,
  selectedTo,
  onSelect,
}: {
  tenantId: string;
  /// The ledger's applied accepted range, as instants; a bucket inside it reads as selected.
  selectedFrom?: string;
  selectedTo?: string;
  onSelect: (from: string, to: string) => void;
}) {
  const [range, setRange] = useState<ActivityRange>("1h");
  const activity = useEventActivity(tenantId, range);
  const buckets = activity.data?.buckets ?? [];

  // Roving focus: the chart is one tab stop, and the arrow keys move between its intervals.
  const [focused, setFocused] = useState(buckets.length - 1);
  const anchor = useRef<number | null>(null);
  const dragging = useRef(false);
  const dragged = useRef(false);
  const buttons = useRef<(HTMLButtonElement | null)[]>([]);

  useEffect(() => {
    // A new range is a new set of intervals: start again from the most recent one.
    setFocused(Math.max(0, buckets.length - 1));
    anchor.current = null;
  }, [buckets.length]);

  useEffect(() => {
    const stop = () => {
      dragging.current = false;
    };
    window.addEventListener("pointerup", stop);
    return () => window.removeEventListener("pointerup", stop);
  }, []);

  function select(from: number, to: number) {
    const [low, high] = from <= to ? [from, to] : [to, from];
    onSelect(buckets[low].start, buckets[high].end);
  }

  function move(to: number) {
    const next = Math.min(buckets.length - 1, Math.max(0, to));
    setFocused(next);
    buttons.current[next]?.focus();
    return next;
  }

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
    if (step !== 0) {
      event.preventDefault();
      if (event.shiftKey) {
        const from = anchor.current ?? index;
        anchor.current = from;
        select(from, move(index + step));
      } else move(index + step);
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      move(event.key === "Home" ? 0 : buckets.length - 1);
    }
  }

  const from = selectedFrom ? new Date(selectedFrom).getTime() : Number.NaN;
  const to = selectedTo ? new Date(selectedTo).getTime() : Number.NaN;
  const isSelected = (bucket: EventActivityBucket) =>
    new Date(bucket.start).getTime() >= (Number.isNaN(from) ? Number.POSITIVE_INFINITY : from) &&
    new Date(bucket.end).getTime() <= (Number.isNaN(to) ? Number.NEGATIVE_INFINITY : to);
  const totalOf = (bucket: EventActivityBucket) =>
    activityOutcomes.reduce((sum, { key }) => sum + Number(bucket[key]), 0);
  const max = Math.max(1, ...buckets.map(totalOf));
  const problem = asProblem(activity.error);

  return (
    <section aria-labelledby="event-activity" className="flex flex-col gap-2.5 rounded-lg border bg-surface p-3.5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-baseline gap-x-3">
          <h2 id="event-activity" className="m-0">
            Activity
          </h2>
          <p className="m-0 text-[13px] text-ink-secondary">
            {activity.data
              ? `${buckets.reduce((sum, bucket) => sum + totalOf(bucket), 0)} Events accepted, by current outcome.`
              : "Events accepted, by current outcome."}
          </p>
        </div>
        <fieldset className="m-0 flex gap-1 border-0 p-0">
          <legend className="sr-only">Activity range</legend>
          {activityRanges.map((option) => (
            <button
              key={option.value}
              type="button"
              aria-pressed={range === option.value}
              onClick={() => setRange(option.value)}
              className="min-h-6 min-w-10 cursor-pointer rounded-md border bg-surface px-2 py-0.5 text-[13px] tabular-nums hover:bg-hover-surface aria-pressed:border-accent-border aria-pressed:bg-selected-surface aria-pressed:text-selected-ink focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              {option.label}
            </button>
          ))}
        </fieldset>
      </div>

      {problem ? (
        <ReadError problem={problem} what="Event activity" />
      ) : !activity.data ? (
        <div className="h-32 rounded-md bg-surface-quiet" aria-busy="true">
          <span className="sr-only">Loading activity…</span>
        </div>
      ) : (
        <>
          {/* Scrolls within the card rather than shrinking an interval below a usable target. */}
          <div className="overflow-x-auto">
            <fieldset className="m-0 flex h-32 min-w-full touch-none items-stretch gap-0.5 border-0 p-0 select-none">
              <legend className="sr-only">
                Event activity intervals. Arrow keys move between them; Shift with an arrow extends the selection.
              </legend>
              {buckets.map((bucket, index) => {
                const total = totalOf(bucket);
                return (
                  <button
                    key={bucket.start}
                    ref={(element) => {
                      buttons.current[index] = element;
                    }}
                    type="button"
                    tabIndex={index === focused ? 0 : -1}
                    aria-label={bucketName(bucket)}
                    aria-pressed={isSelected(bucket)}
                    onFocus={() => setFocused(index)}
                    onKeyDown={(event) => onKeyDown(event, index)}
                    onPointerDown={(event) => {
                      // Touch captures the pointer to the first bar; release it so the drag reaches the others.
                      event.currentTarget.releasePointerCapture?.(event.pointerId);
                      dragging.current = true;
                      dragged.current = false;
                      anchor.current = index;
                    }}
                    onPointerEnter={() => {
                      if (!dragging.current || anchor.current === null || anchor.current === index) return;
                      dragged.current = true;
                      select(anchor.current, index);
                    }}
                    onClick={() => {
                      if (dragged.current) {
                        dragged.current = false;
                        return;
                      }
                      anchor.current = index;
                      select(index, index);
                    }}
                    className="group flex min-w-6 flex-1 cursor-pointer flex-col-reverse rounded-sm px-px pt-1 hover:bg-hover-surface aria-pressed:bg-selected-surface focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
                  >
                    {total === 0 ? <span className="h-px w-full bg-accent-border" /> : null}
                    {activityOutcomes.map(({ key, swatch }) =>
                      Number(bucket[key]) > 0 ? (
                        <span
                          key={key}
                          className={cn("w-full", swatch)}
                          style={{ height: `${(Number(bucket[key]) / max) * 100}%` }}
                        />
                      ) : null,
                    )}
                  </button>
                );
              })}
            </fieldset>
          </div>
          <div className="flex justify-between text-xs text-ink-secondary tabular-nums">
            <span>{bucketTime.format(new Date(activity.data.window_start))}</span>
            <span>{bucketTime.format(new Date(activity.data.window_end))}</span>
          </div>
          <ul className="m-0 flex list-none flex-wrap gap-x-3.5 gap-y-1 p-0 text-xs text-ink-secondary">
            {activityOutcomes.map(({ key, label, swatch }) => (
              <li key={key} className="flex items-center gap-1.5">
                <span aria-hidden="true" className={cn("inline-block size-2.5 rounded-xs", swatch)} />
                {label}
              </li>
            ))}
            <li>Select an interval, or drag across several, to scope the ledger to that time.</li>
          </ul>
        </>
      )}
    </section>
  );
}
