import { cn } from "cn";
import type { ReactNode } from "react";

/// A status as the Admin API names it, rendered so its word is always present and its colour is
/// only the second cue. Nothing here is ever communicated by colour alone.
///
/// The palette defines exactly two semantic pairs — attention and failure — so everything else is
/// quiet. That scarcity is the point: if a normal state carried colour too, the one state needing an
/// Operator would stop standing out.
type Tone = "quiet" | "attention" | "failure";

const tones: Record<Tone, string> = {
  quiet: "border-border bg-surface-quiet text-ink-secondary",
  attention: "border-warning-surface bg-warning-surface text-warning-ink",
  failure: "border-danger-surface bg-danger-surface text-danger-ink",
};

/// Attention is a state an Operator may need to act on; failure is one the platform has stopped
/// retrying. `unrouted` is attention rather than failure: the Event was accepted and matched no
/// Subscription, which is the established signal for a missing or misconfigured Subscription rather
/// than a delivery that failed. A `disabled` Tenant or Connector is quiet — deliberate configuration
/// is not a fault.
///
/// An unlisted status is quiet. A status this map has never seen is not evidence of a problem, and
/// guessing a colour for it would be inventing meaning the API did not send.
const toneFor: Record<string, Tone> = {
  processing: "attention",
  unrouted: "attention",
  pending: "attention",
  in_flight: "attention",
  failed: "failure",
  dead_lettered: "failure",
  revoked: "failure",
  expired: "failure",
};

/// The domain spells statuses in snake case; an Operator reads them as words. Only the two that do
/// not survive a plain underscore swap are named here.
const labels: Record<string, string> = {
  dead_lettered: "Dead-lettered",
  in_flight: "In flight",
};

export function statusLabel(status: string): string {
  const spelled = labels[status];
  if (spelled) return spelled;
  const words = status.replace(/_/g, " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/// `children` replaces the label where the badge is counting rather than naming — "3 dead-lettered"
/// in a Delivery-count cell — while keeping the tone the status resolves to.
export function StatusBadge({
  status,
  children,
  className,
}: {
  status: string;
  children?: ReactNode;
  className?: string;
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center rounded-md border px-2 py-0.5 text-xs font-medium whitespace-nowrap",
        tones[toneFor[status] ?? "quiet"],
        className,
      )}
    >
      {children ?? statusLabel(status)}
    </span>
  );
}
