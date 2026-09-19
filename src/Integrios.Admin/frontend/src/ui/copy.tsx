import { cn } from "cn";
import { Check, Copy, X } from "lucide-react";
import { type ReactNode, useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { CodeBlock, type CodeLanguage } from "./codeHighlight";

type CopyState = "idle" | "copied" | "failed";

/// Ink only, and on hover too: the pointer is still on the button when the outcome appears.
const tone: Record<CopyState, string | undefined> = {
  idle: undefined,
  copied: "text-success-ink hover:text-success-ink",
  failed: "text-danger-ink hover:text-danger-ink",
};

/// One copy, three controls. The outcome shows on the control the Operator just pressed and clears
/// itself, so a "copied" left over from minutes ago cannot vouch for what is on the clipboard now.
/// Clipboard access can be missing or refused; then the text is selected instead, so a keyboard copy
/// still finishes the job and the control is never a dead end.
function useCopy(label: string) {
  const [state, setState] = useState<CopyState>("idle");
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);

  const settle = (next: CopyState) => {
    setState(next);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setState("idle"), 2000);
  };
  const copy = (text: string, select: () => void) => {
    const failed = () => {
      select();
      settle("failed");
    };
    if (!navigator.clipboard) return failed();
    navigator.clipboard.writeText(text).then(() => settle("copied"), failed);
  };
  const announcement =
    state === "copied"
      ? `${label} copied.`
      : state === "failed"
        ? `Could not copy the ${label.toLowerCase()}. It is selected; press Ctrl+C to copy it.`
        : "";
  return { state, copy, announcement, tone: tone[state] };
}

/// Selects a rendered value so a keyboard copy takes exactly it.
function selectContents(element: HTMLElement | null) {
  if (element) window.getSelection()?.selectAllChildren(element);
}

/// The visible half of a copy outcome, for a button that carries words.
function CopyLabel({ state, idle }: { state: CopyState; idle: string }) {
  if (state === "copied")
    return (
      <>
        <Check aria-hidden="true" />
        Copied
      </>
    );
  if (state === "failed")
    return (
      <>
        <X aria-hidden="true" />
        Copy failed
      </>
    );
  return <>{idle}</>;
}

/// An opaque value an Operator has to get out of the dashboard and into something else — a trace
/// identity pasted into whatever observability backend the deployment runs, an identifier quoted in
/// a ticket. The dashboard hands it over and knows nothing about where it is going.
///
/// The value stays in a read-only field rather than plain text on purpose: clipboard access can be
/// unavailable or refused, and selecting the field still lets the Operator copy by hand, so the
/// control is never a dead end.
/// `action` sits beside the copy control, for a second thing to do with the same value.
export function CopyValue({
  id,
  label,
  value,
  action,
}: {
  id: string;
  label: string;
  value: string;
  action?: ReactNode;
}) {
  const { state, copy, announcement, tone } = useCopy(label);
  const field = useRef<HTMLInputElement>(null);

  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} className="font-mono text-sm" ref={field} readOnly value={value} />
      <div className="flex flex-wrap items-center gap-3">
        <Button
          type="button"
          variant="outline"
          className={tone}
          onClick={() => copy(value, () => field.current?.select())}
        >
          <CopyLabel state={state} idle={`Copy ${label.toLowerCase()}`} />
        </Button>
        {action}
        <span role="status" className="sr-only">
          {announcement}
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
///
/// Nothing is ever clipped. An identifier that ends in an ellipsis cannot be read back or compared
/// against one in a ticket, and the copy control does not help an Operator who cannot see which of
/// two ids they are copying. A panel wraps it; a ledger keeps it on one line and lets the card the
/// table sits in scroll, which is the width that ledger already absorbs for every other column.
export function CopyInline({ label, value, oneLine }: { label: string; value: string; oneLine?: boolean }) {
  const { state, copy, announcement, tone } = useCopy(label);
  const shown = useRef<HTMLSpanElement>(null);
  const Icon = state === "copied" ? Check : state === "failed" ? X : Copy;

  return (
    <span className="flex items-center gap-1">
      <span ref={shown} className={cn("font-mono", oneLine ? "whitespace-nowrap" : "block break-all")}>
        {value}
      </span>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        aria-label={`Copy ${label.toLowerCase()}`}
        // An outcome stays visible after the pointer leaves, or it would vanish with the button.
        className={cn(
          "size-6 shrink-0 p-0 opacity-0 group-hover:opacity-100 focus-visible:opacity-100",
          state === "idle" ? undefined : "opacity-100",
          tone,
        )}
        onClick={() => copy(value, () => selectContents(shown.current))}
      >
        <Icon aria-hidden="true" className="size-3.5" />
      </Button>
      <span role="status" className="sr-only">
        {announcement}
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
  copyable = true,
  unbounded,
  language,
}: {
  label: string;
  value: unknown;
  /// The grammar this panel's value is written in. Left out, a JSON document is recognized as one
  /// and everything else is shown unpainted.
  language?: CodeLanguage;
  truncated?: boolean;
  note?: string;
  copyable?: boolean;
  /// A stored body is capped because a destination can return 8 KiB of anything, and a panel that
  /// tall would bury whatever follows it. A document the dashboard generated is bounded by the form
  /// that produced it, so it is shown whole and the surface it sits in does the scrolling — one
  /// scroll region rather than a wheel that stops working over the panel.
  unbounded?: boolean;
}) {
  const text = typeof value === "string" ? value : JSON.stringify(value, null, 2);
  const { state, copy, announcement, tone } = useCopy(label);
  const shown = useRef<HTMLDivElement>(null);

  return (
    <section className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h4 className="m-0 text-sm font-semibold">{label}</h4>
        <div className="flex items-center gap-2">
          {truncated ? (
            <span className="rounded-full bg-warning-surface px-2 py-0.5 text-xs text-warning-ink">Truncated</span>
          ) : null}
          {copyable ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              aria-label={`Copy ${label.toLowerCase()}`}
              className={tone}
              onClick={() => copy(text, () => selectContents(shown.current))}
            >
              <CopyLabel state={state} idle="Copy" />
            </Button>
          ) : null}
        </div>
      </div>
      <div ref={shown} className="min-w-0">
        <CodeBlock value={value} language={language} className={unbounded ? undefined : "max-h-64 overflow-auto"} />
      </div>
      {truncated ? (
        <p className="m-0 text-xs text-ink-secondary">
          Only the first 8 KiB the destination returned is stored. {note}
        </p>
      ) : note ? (
        <p className="m-0 text-xs text-ink-secondary">{note}</p>
      ) : null}
      <span role="status" className="sr-only">
        {announcement}
      </span>
    </section>
  );
}
