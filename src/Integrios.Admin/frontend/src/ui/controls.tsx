import { LoaderCircle } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { type ComponentProps, type ReactNode, useState } from "react";
import { Link, useLocation, useSearchParams } from "react-router";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetHeader, SheetTrigger } from "@/components/ui/sheet";
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
/// Creating something opens a sheet from the trailing edge rather than a panel above the list. The
/// list keeps its width and its position - measured on Connections at 1512, the alternatives moved
/// it 657 pixels down the page or took 73 pixels off it - and the half-filled form is a dialog the
/// Operator dismisses rather than a region of the page they have to scroll past.
///
/// The trigger and the sheet are one element so a screen hands the page header a single action, and
/// the content is portalled out of the layout it would otherwise sit inside.
function FormSheet({
  label,
  title = label,
  description,
  initialOpen = false,
  variant,
  children,
}: {
  label: string;
  title?: string;
  description?: string;
  initialOpen?: boolean;
  variant: "default" | "outline";
  /// Handed a way to close, because a write that succeeded should not leave its own form standing.
  children: (close: () => void) => ReactNode;
}) {
  const [open, setOpen] = useState(initialOpen);

  return (
    <Sheet open={open} onOpenChange={setOpen}>
      <SheetTrigger asChild>
        <Button type="button" variant={variant}>
          {label}
        </Button>
      </SheetTrigger>
      <SheetContent aria-label={title}>
        <SheetHeader title={title} description={description} />
        {children(() => setOpen(false))}
      </SheetContent>
    </Sheet>
  );
}

export function CreateSheet(props: Omit<ComponentProps<typeof FormSheet>, "variant">) {
  return <FormSheet {...props} variant="default" />;
}

/// Editing opens the same way creating does. A detail panel is for reading what is stored; changing
/// it is a task with its own surface, and running the two together is what made the panel long
/// enough to bury the destructive action underneath a form nobody had asked to open.
export function EditSheet(props: Omit<ComponentProps<typeof FormSheet>, "variant">) {
  return <FormSheet {...props} variant="outline" />;
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
/// Confirmation is modal so its full question never reflows the compact action area that opened it;
/// Radix moves focus in, traps it, answers Escape, and restores it to the trigger on close.
export function ConfirmAction({
  label,
  question,
  consequence,
  confirmLabel,
  busy,
  variant = "destructive",
  onConfirm,
}: {
  label: string;
  question: string;
  /// Destructive by default, because that is what an irreversible action usually is here — it takes
  /// a capability away. Recovery is the exception: replaying a dead-lettered Delivery is irreversible
  /// too, and confirmed for that reason, but it restores work rather than removing it, so it must not
  /// wear the colour that means something is being taken away.
  variant?: "destructive" | "outline";
  /// What the action does to everything around it, read in the confirmation alongside the question
  /// it answers. Opening the dialog is not the commitment — cancelling is free, and the destructive
  /// button is a second, separate press — so this still reaches an Operator while they are deciding,
  /// without a standing red panel on every screen that happens to carry a destructive action.
  consequence?: string;
  confirmLabel?: string;
  busy?: boolean;
  onConfirm: () => void;
}) {
  const [open, setOpen] = useState(false);

  return (
    <DialogPrimitive.Root open={open} onOpenChange={setOpen}>
      <DialogPrimitive.Trigger asChild>
        <Button type="button" variant={variant} className="self-start" disabled={busy}>
          {label}
        </Button>
      </DialogPrimitive.Trigger>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-ink/20" />
        <DialogPrimitive.Content className="fixed top-1/2 left-1/2 z-50 flex w-[calc(100%-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 flex-col gap-4 rounded-lg border bg-surface p-5 shadow-[0_24px_64px_-32px_rgb(23_23_23/0.45)] outline-none">
          <div>
            <DialogPrimitive.Title className="m-0">{label}</DialogPrimitive.Title>
            <DialogPrimitive.Description className="m-0 mt-2 text-sm text-ink-secondary">
              {question}
            </DialogPrimitive.Description>
          </div>
          {consequence ? <p className="m-0 text-sm text-danger-ink">{consequence}</p> : null}
          <div className="flex flex-wrap justify-end gap-2">
            <DialogPrimitive.Close asChild>
              <Button type="button" variant="outline">
                Cancel
              </Button>
            </DialogPrimitive.Close>
            <Button
              type="button"
              variant={variant}
              disabled={busy}
              onClick={() => {
                setOpen(false);
                onConfirm();
              }}
            >
              {confirmLabel ?? label}
            </Button>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}

/// Plural by the one rule English keeps: a trailing sibilant takes -es. Every noun a list here
/// counts is a domain word the product chose, so nothing irregular reaches this.
function plural(noun: string): string {
  return /(s|x|z|ch|sh)$/i.test(noun) ? `${noun}es` : `${noun}s`;
}

/// The one way further rows are read: an explicit request for the next cursor, never infinite
/// scroll and never a page number.
/// Forward-only paging, stated in the terms the cursor actually supports: how many rows are loaded,
/// and whether there are more. Never a total or a page number — the Admin API issues neither.
export function LoadMore({
  hasMore,
  busy,
  loaded,
  noun,
  onLoadMore,
}: {
  hasMore: boolean;
  busy: boolean;
  loaded?: number;
  /// What was counted, singular. A footer that says "3 rows" makes the reader look up to remember
  /// what a row is here; naming the thing costs one word and answers it.
  noun: string;
  onLoadMore: () => void;
}) {
  if (!hasMore && loaded === undefined) return null;
  return (
    <>
      <span className="text-sm text-ink-secondary">
        {loaded === undefined ? null : `Showing ${loaded} ${loaded === 1 ? noun : plural(noun)}`}
      </span>
      {hasMore ? (
        <Button type="button" variant="outline" size="sm" onClick={onLoadMore} disabled={busy}>
          Load more
        </Button>
      ) : null}
    </>
  );
}

/// A spinner over the whole document column while the list is being read. It is positioned against
/// `<main>` rather than against the list, so it covers the page an Operator is waiting on and stops
/// at the rail — which stays visible and operable, because navigating away is the one thing worth
/// doing while a slow read is outstanding.
///
/// Being out of flow, it does not hold the rows' place: the page is this spinner and then it is the
/// loaded screen. That is the trade a covering spinner makes against a placeholder shaped like the
/// content, and it is why the cover is deferred rather than instant.
///
/// The word beside it is not decoration: the platform's reduced-motion rule cuts every animation to
/// a single imperceptible step, which leaves an Operator who asked for less motion looking at a
/// stationary circle. The label is what still says "loading" for them, and for anyone reading by ear.
///
/// Only the cover defers its reveal, and the announcement is not deferred with it: most reads answer
/// faster than the delay, and a spinner that appears and vanishes inside a tenth of a second is the
/// flicker this is here to avoid.
function ListSkeleton() {
  return (
    <div
      role="status"
      aria-busy="true"
      className="animate-deferred-reveal absolute inset-0 z-20 flex items-center justify-center gap-2.5 bg-canvas text-ink-secondary"
    >
      <LoaderCircle aria-hidden="true" className="size-5 animate-spin" />
      <span className="text-sm">Loading…</span>
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
    // A wrapping row of compact controls rather than a grid of labelled fields: each control is one
    // line tall and carries its own value, so the bar costs the list a single row of height and an
    // Operator reads the whole scope in one pass. How many are applied is stated on the list's own
    // caption, where the rows it produced are.
    <section aria-label="Filters" className="flex flex-wrap items-center gap-2">
      {children}
      {applied > 0 ? (
        onClear ? (
          <Button type="button" variant="ghost" onClick={onClear}>
            Clear filters
          </Button>
        ) : (
          <ClearFilters />
        )
      ) : null}
    </section>
  );
}

/// How many filters a list is under, in the words its caption already uses. Empty when the list is
/// showing everything, so an unfiltered caption says nothing extra.
export function appliedNote(applied: number): string {
  if (applied === 0) return "";
  return ` · ${applied} filter${applied === 1 ? "" : "s"} applied`;
}

function ClearFilters({ size }: { size?: "sm" }) {
  const [params] = useSearchParams();
  const { pathname } = useLocation();

  if (params.toString() === "") return null;
  return (
    <Button asChild variant="ghost" size={size}>
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
  if (loaded && empty) return <p className="m-0">{emptyText}</p>;
  return null;
}
