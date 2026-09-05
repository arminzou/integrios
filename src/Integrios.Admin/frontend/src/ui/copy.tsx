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
