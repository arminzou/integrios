import { cn } from "cn";
import type { ReactNode } from "react";

/// A status as the Admin API names it, rendered so its word is always present and its colour is
/// only the second cue. Nothing here is ever communicated by colour alone.
///
/// The palette defines three semantic pairs — success, attention and failure — and everything else
/// is quiet. Success is the deliberate exception to spending colour only on what needs an Operator:
/// a Delivery timeline is read as a column of outcomes, and telling a settled success from a settled
/// failure at a glance is the whole job of that column. Failure is the brightest of the three so it
/// still wins a row that also carries green, and every state that is merely normal — an `active`
/// Tenant, an `accepted` Event — stays quiet rather than joining in.
type Tone = "quiet" | "success" | "attention" | "failure";

const tones: Record<Tone, string> = {
  quiet: "border-border bg-surface-quiet text-ink-secondary",
  success: "border-success-surface bg-success-surface text-success-ink",
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
/// A delivery attempt spells the same two unsettled states its Delivery does: `in_progress` is
/// `in_flight` at attempt granularity, and `indeterminate` is the platform saying it could not
/// establish whether the attempt landed — unresolved rather than failed, which is the same reason
/// `unrouted` is attention. `succeeded` is the one settled-and-fine state that carries colour, for
/// the reason the tone table gives: an outcome column is only scannable if both outcomes are read
/// at a glance, not just the bad one.
const toneFor: Record<string, Tone> = {
  processing: "attention",
  unrouted: "attention",
  pending: "attention",
  in_flight: "attention",
  in_progress: "attention",
  indeterminate: "attention",
  succeeded: "success",
  failed: "failure",
  dead_lettered: "failure",
  revoked: "failure",
  expired: "failure",
};

function statusTone(status: string): Tone {
  return toneFor[status] ?? "quiet";
}

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

const markers: Record<Tone, string> = {
  quiet: "before:bg-selected-ink",
  success: "before:bg-success-ink",
  attention: "before:bg-surface before:ring-2 before:ring-warning-ink before:ring-inset",
  failure: "before:bg-danger-ink",
};

/// The marker on a timeline entry, reading the same table the badge beside it reads, so the two
/// cannot disagree about an outcome. An unsettled attempt is drawn hollow as well as tinted, so the
/// three states differ by shape and not only by colour where they are read as a column of markers
/// rather than one at a time.
export function statusMarker(status: string): string {
  return markers[statusTone(status)];
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
