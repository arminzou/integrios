import { type ReactNode, useEffect, useRef, useState } from "react";
import { Link, useLocation, useSearchParams } from "react-router";
import { Button } from "@/components/ui/button";
import type { Problem } from "../api/problem";

/// A collapsed "Find an X" filter panel or "New X" create panel, shared across every capability's
/// list screen so neither a filter form nor a create form permanently dominates the page above the
/// list it belongs to.
export function Disclosure({ label, children }: { label: string; children: ReactNode }) {
  return (
    <details className="group">
      <summary className="inline-flex w-fit cursor-pointer list-none items-center gap-2 rounded-md border bg-surface px-3 py-2 text-sm font-medium outline-none select-none hover:bg-hover-surface focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 [&::-webkit-details-marker]:hidden">
        <svg
          aria-hidden="true"
          viewBox="0 0 12 12"
          className="size-3 shrink-0 transition-transform group-open:rotate-90"
        >
          <path
            d="M4.5 2.5 8 6l-3.5 3.5"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
        {label}
      </summary>
      <div className="pt-4">{children}</div>
    </details>
  );
}

/// The create panel's open state, lifted so the control that opens it can sit in the page header
/// where a primary action belongs, while the form itself stays below the header in reading order.
///
/// The panel is rendered and hidden rather than unmounted, so `aria-controls` always resolves to a
/// real element and the browser announces the relationship whether or not it is open.
export function useCreatePanel(id: string) {
  const [open, setOpen] = useState(false);
  return {
    open,
    close: () => setOpen(false),
    triggerProps: {
      type: "button" as const,
      "aria-expanded": open,
      "aria-controls": id,
      onClick: () => setOpen((value) => !value),
    },
    panelProps: { id, hidden: !open },
  };
}

/// Attributes that tie a control to its own label and error message. Screens spread these onto the
/// control itself so a failed field announces its message rather than only turning a colour.
export function fieldProps(id: string, error?: string, hasHint = false) {
  const describedBy = [hasHint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(" ");
  return {
    id,
    "aria-invalid": error ? (true as const) : undefined,
    "aria-describedby": describedBy || undefined,
  };
}

export function Field({
  id,
  label,
  error,
  hint,
  children,
}: {
  id: string;
  label: string;
  error?: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <p>
      <label htmlFor={id}>{label}</label>
      {children}
      {hint ? <span id={`${id}-hint`}>{hint}</span> : null}
      {error ? (
        <strong id={`${id}-error`} role="alert">
          {error}
        </strong>
      ) : null}
    </p>
  );
}

export function FormError({ message }: { message?: string }) {
  return message ? (
    <p role="alert" className="text-sm text-destructive">
      {message}
    </p>
  ) : null;
}

/// An irreversible action states what it is about to change, by name, before it can be confirmed.
/// The confirmation takes focus so it is reachable and announced without a pointer.
export function ConfirmAction({
  label,
  question,
  confirmLabel,
  busy,
  onConfirm,
}: {
  label: string;
  question: string;
  confirmLabel?: string;
  busy?: boolean;
  onConfirm: () => void;
}) {
  const [armed, setArmed] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);
  const restoreFocus = useRef(false);

  useEffect(() => {
    if (armed) confirmRef.current?.focus();
    else if (restoreFocus.current) {
      restoreFocus.current = false;
      triggerRef.current?.focus();
    }
  }, [armed]);

  if (!armed)
    return (
      <Button
        ref={triggerRef}
        type="button"
        variant="outline"
        className="self-start"
        disabled={busy}
        onClick={() => setArmed(true)}
      >
        {label}
      </Button>
    );

  return (
    // biome-ignore lint/a11y/useSemanticElements: a <fieldset> needs a <legend> and is a form-control grouping; this is an inline confirmation named by aria-label
    <span role="group" aria-label={label} className="flex flex-wrap items-center gap-3">
      <span>{question}</span>
      <Button
        type="button"
        variant="destructive"
        ref={confirmRef}
        disabled={busy}
        onClick={() => {
          setArmed(false);
          onConfirm();
        }}
      >
        {confirmLabel ?? label}
      </Button>
      <Button
        type="button"
        variant="outline"
        onClick={() => {
          restoreFocus.current = true;
          setArmed(false);
        }}
      >
        Cancel
      </Button>
    </span>
  );
}

/// The one way further rows are read: an explicit request for the next cursor, never infinite
/// scroll and never a page number.
/// Forward-only paging, stated in the terms the cursor actually supports: how many rows are loaded,
/// and whether there are more. Never a total or a page number — the Admin API issues neither.
export function LoadMore({
  hasMore,
  busy,
  loaded,
  onLoadMore,
}: {
  hasMore: boolean;
  busy: boolean;
  loaded?: number;
  onLoadMore: () => void;
}) {
  if (!hasMore && loaded === undefined) return null;
  return (
    <>
      <span className="text-sm text-ink-secondary">
        {loaded === undefined ? null : `Showing ${loaded} ${loaded === 1 ? "row" : "rows"}`}
      </span>
      {hasMore ? (
        <Button type="button" variant="outline" size="sm" onClick={onLoadMore} disabled={busy}>
          Load more
        </Button>
      ) : null}
    </>
  );
}

/// The rhythm the rows will occupy, so a list does not jump when they land. Plain bars rather than a
/// table: a table with no rows in it would announce columns and headers that are not there yet, so
/// the bars are decorative and the wrapper carries the announcement instead. The pulse is an opacity
/// change, and the platform's reduced-motion rule already removes it.
function ListSkeleton() {
  return (
    <div role="status" aria-busy="true">
      <span className="sr-only">Loading…</span>
      <div aria-hidden="true" className="overflow-hidden rounded-lg border bg-card">
        {Array.from({ length: 5 }, (_, row) => (
          // biome-ignore lint/suspicious/noArrayIndexKey: fixed-length placeholder rows with no identity
          <div key={row} className="flex gap-4 border-b px-4 py-3 last:border-b-0">
            {Array.from({ length: 4 }, (_, cell) => (
              // biome-ignore lint/suspicious/noArrayIndexKey: fixed-length placeholder cells with no identity
              <div key={cell} className="h-4 flex-1 animate-pulse rounded bg-surface-quiet" />
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}

/// A write that only stops being busy leaves an Operator guessing whether it landed. This says so,
/// and is announced rather than only shown. The element is always rendered so the live region exists
/// before its content changes, which is what makes the change announced at all.
///
/// The wording names what changed in the same words as the control that changed it: a button reading
/// "Deactivate Tenant" reports "Tenant deactivated", never a generic "Success".
export function WriteStatus({ done, children }: { done: boolean; children: ReactNode }) {
  return (
    <p role="status" className="m-0 text-sm text-ink-secondary">
      {done ? children : null}
    </p>
  );
}

/// The next action for a list that is empty because of what was asked of it, rather than because
/// the Tenant holds nothing. It reads the URL rather than any screen's own state, which is what
/// lets every capability offer it without wiring one per screen — and is only possible because the
/// filters live in the URL at all.
/// A list's current scope, stated rather than hidden behind a disclosure. An Operator triaging a
/// failed Delivery has to be able to tell a filtered ledger from an unfiltered one without clicking
/// anything, and the count says it in words rather than by a border colour alone.
///
/// The disclosure this replaced still carries create panels: authoring is a thing an Operator opens
/// deliberately, while scope is something the screen owes them at all times.
export function FilterBar({
  applied,
  children,
  onClear,
}: {
  applied: number;
  children: ReactNode;
  onClear?: () => void;
}) {
  return (
    <section aria-label="Filters" className="flex flex-col gap-3">
      <div className="flex flex-wrap items-end gap-x-4 gap-y-3">{children}</div>
      {applied > 0 ? (
        <p className="m-0 flex flex-wrap items-center gap-3 text-sm text-ink-secondary">
          <span>
            {applied} filter{applied === 1 ? "" : "s"} applied.
          </span>
          {onClear ? (
            <Button type="button" variant="outline" size="sm" onClick={onClear}>
              Clear filters
            </Button>
          ) : (
            <ClearFilters size="sm" />
          )}
        </p>
      ) : null}
    </section>
  );
}

function ClearFilters({ size }: { size?: "sm" }) {
  const [params] = useSearchParams();
  const { pathname } = useLocation();

  if (params.toString() === "") return null;
  return (
    <Button asChild variant="outline" size={size}>
      {/* The base layer hands links their underline back, which a control shaped like a button
          should not carry. */}
      <Link to={pathname} className="no-underline">
        Clear filters
      </Link>
    </Button>
  );
}

/// What a list shows when it has no rows to show: still loading, failed, or genuinely empty. The
/// empty text names the scope that was searched so an empty Tenant is not mistaken for a failure.
export function ListStatus({
  busy,
  loaded,
  problem,
  empty,
  emptyText,
}: {
  busy: boolean;
  loaded: boolean;
  problem: Problem | null;
  empty: boolean;
  emptyText: string;
}) {
  if (problem) return <p role="alert">{problem.detail ?? `The list could not be read (${problem.status}).`}</p>;
  if (busy && !loaded) return <ListSkeleton />;
  if (loaded && empty)
    return (
      <div className="flex flex-col items-start gap-3">
        <p className="m-0">{emptyText}</p>
        <ClearFilters />
      </div>
    );
  return null;
}
