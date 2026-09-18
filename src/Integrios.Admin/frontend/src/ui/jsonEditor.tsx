import { Button } from "@/components/ui/button";
import { CodeTextarea } from "./codeHighlight";
import { formatJson, parseJson } from "./json";

/// A labelled JSON document with the action that formats it.
export function JsonEditor({
  id,
  label,
  value,
  invalid,
  onChange,
}: {
  id: string;
  label: string;
  value: string;
  invalid: boolean;
  onChange: (value: string) => void;
}) {
  const parsed = parseJson(value);
  const formatted = parsed.error === undefined ? formatJson(parsed.value) : null;

  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <div className="flex items-center justify-between gap-2">
        <label htmlFor={id} className="text-sm font-medium">
          {label}
        </label>
        {/* Nothing to do when the document is already what formatting would produce, and nothing it
            can do when the document does not parse. */}
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={formatted === null || formatted === value}
          onClick={() => formatted !== null && onChange(formatted)}
        >
          Format
        </Button>
      </div>
      {/* Bounded, and it scrolls on its own. A sample is as long as the provider makes it, and an
          editor that grew with one pushed the dialog's Preview and Use configuration past the foot
          of the window — so reading the sample cost the Operator the actions it was read for.
          The height is capped against the viewport as well, because the short screens are where
          that happened first. */}
      <CodeTextarea
        id={id}
        aria-invalid={invalid}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="h-[min(24rem,50vh)]"
      />
    </div>
  );
}
