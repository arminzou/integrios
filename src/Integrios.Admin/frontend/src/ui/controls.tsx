import { type ReactNode, useEffect, useRef, useState } from "react";
import type { Problem } from "../api/problem";
import { navigate } from "../routes";

/// A collapsed "Find an X" filter panel or "New X" create panel, shared across every capability's
/// list screen so neither a filter form nor a create form permanently dominates the page above the
/// list it belongs to.
export function Disclosure({ label, children }: { label: string; children: ReactNode }) {
  return (
    <details className="disclosure">
      <summary>{label}</summary>
      {children}
    </details>
  );
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
  return message ? <p role="alert">{message}</p> : null;
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
      <button ref={triggerRef} type="button" disabled={busy} onClick={() => setArmed(true)}>
        {label}
      </button>
    );

  return (
    <span role="group" aria-label={label}>
      <span>{question}</span>
      <button
        type="button"
        ref={confirmRef}
        disabled={busy}
        onClick={() => {
          setArmed(false);
          onConfirm();
        }}
      >
        {confirmLabel ?? label}
      </button>
      <button
        type="button"
        onClick={() => {
          restoreFocus.current = true;
          setArmed(false);
        }}
      >
        Cancel
      </button>
    </span>
  );
}

export function LoadMore({
  cursor,
  busy,
  onLoadMore,
}: {
  cursor: string | null;
  busy: boolean;
  onLoadMore: () => void;
}) {
  if (!cursor) return null;
  return (
    <button type="button" onClick={onLoadMore} disabled={busy}>
      Load more
    </button>
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
  if (busy && !loaded) return <p>Loading…</p>;
  if (loaded && empty) return <p>{emptyText}</p>;
  return null;
}

/// A real anchor, so copying, opening in a new tab, and middle-clicking behave as the Operator
/// expects; only a plain left click is taken over to avoid a full document reload. `current` marks
/// the active navigation destination with `aria-current`, the same signal assistive technology and
/// the selected-surface style key off.
export function Link({ to, children, current }: { to: string; children: ReactNode; current?: boolean }) {
  return (
    <a
      href={to}
      aria-current={current ? "page" : undefined}
      onClick={(event) => {
        if (event.defaultPrevented || event.button !== 0) return;
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        navigate(to);
      }}
    >
      {children}
    </a>
  );
}
