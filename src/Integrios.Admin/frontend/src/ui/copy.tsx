import { useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

/// An opaque value an Operator has to get out of the dashboard and into something else — a trace
/// identity pasted into whatever observability backend the deployment runs, an identifier quoted in
/// a ticket. The dashboard hands it over and knows nothing about where it is going.
///
/// The value stays in a read-only field rather than plain text on purpose: clipboard access can be
/// unavailable or refused, and selecting the field still lets the Operator copy by hand, so the
/// control is never a dead end. The confirmation is announced rather than only shown, because the
/// visible change is a few words appearing beside a button that already looked the same before.
export function CopyValue({ id, label, value }: { id: string; label: string; value: string }) {
  const [copied, setCopied] = useState(false);
  const field = useRef<HTMLInputElement>(null);

  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} className="font-mono text-sm" ref={field} readOnly value={value} />
      <div className="flex flex-wrap items-center gap-3">
        <Button
          type="button"
          variant="outline"
          onClick={() => {
            field.current?.select();
            void navigator.clipboard
              ?.writeText(value)
              .then(() => setCopied(true))
              .catch(() => setCopied(false));
          }}
        >
          Copy {label.toLowerCase()}
        </Button>
        <span role="status" className="text-sm text-ink-secondary">
          {copied ? `${label} copied.` : ""}
        </span>
      </div>
    </div>
  );
}

/// An identifier in a dense ledger, with the control to copy it appearing on hover or keyboard
/// focus. The control keeps its space either way, so the column does not reflow as the pointer
/// moves down the list — a ledger that shifts under the cursor is harder to read than one carrying
/// a little unused width.
///
/// It stays reachable without a pointer: `group-focus-within` shows it once tabbing reaches it, so
/// it is never a hover-only affordance.
export function CopyInline({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false);

  return (
    <span className="flex items-center gap-1">
      <span className="truncate font-mono text-sm">{value}</span>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        aria-label={`Copy ${label.toLowerCase()}`}
        className="size-6 shrink-0 p-0 opacity-0 group-hover:opacity-100 focus-visible:opacity-100"
        onClick={() => {
          void navigator.clipboard
            ?.writeText(value)
            .then(() => setCopied(true))
            .catch(() => setCopied(false));
        }}
      >
        <svg aria-hidden="true" viewBox="0 0 16 16" className="size-3.5">
          <path
            d="M5.5 5.5V3.5A1 1 0 0 1 6.5 2.5h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-2M3.5 5.5h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-6a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1Z"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </Button>
      <span role="status" className="sr-only">
        {copied ? `${label} copied.` : ""}
      </span>
    </span>
  );
}

/// A stored body, shown as what it is. JSON is rendered indented because an Operator reading a
/// failed Delivery is looking for one field in a shape they did not write; anything that does not
/// parse is shown exactly as stored, because a destination that returns HTML or a bare string is
/// itself the finding.
///
/// Truncation is stated rather than left to be inferred from length. A fragment presented as a
/// whole body is worse than no body at all: it reads as a complete response that happens to end
/// strangely.
export function BodyPanel({
  label,
  value,
  truncated,
  note,
}: {
  label: string;
  value: unknown;
  truncated?: boolean;
  note?: string;
}) {
  const text = typeof value === "string" ? value : JSON.stringify(value, null, 2);
  const [copied, setCopied] = useState(false);

  return (
    <section className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h4 className="m-0 text-sm font-semibold">{label}</h4>
        <div className="flex items-center gap-2">
          {truncated ? (
            <span className="rounded-full border border-warning-surface bg-warning-surface px-2 py-0.5 text-xs text-warning-ink">
              Truncated
            </span>
          ) : null}
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => {
              void navigator.clipboard
                ?.writeText(text)
                .then(() => setCopied(true))
                .catch(() => setCopied(false));
            }}
          >
            Copy
          </Button>
        </div>
      </div>
      <pre className="m-0 max-h-64 overflow-auto rounded-md border bg-surface-quiet p-3 font-mono text-xs whitespace-pre-wrap">
        {text}
      </pre>
      {truncated ? (
        <p className="m-0 text-xs text-ink-secondary">
          Only the first 8 KiB the destination returned is stored. {note}
        </p>
      ) : note ? (
        <p className="m-0 text-xs text-ink-secondary">{note}</p>
      ) : null}
      <span role="status" className="sr-only">
        {copied ? `${label} copied.` : ""}
      </span>
    </section>
  );
}
