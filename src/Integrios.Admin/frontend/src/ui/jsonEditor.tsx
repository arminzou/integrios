import { Button } from "@/components/ui/button";
import { highlightCode } from "./codeHighlight";
import { formatJson, parseJson } from "./json";

/// A textarea that reads like code. The document is painted once, highlighted, by a `<pre>` in
/// normal flow; the real textarea sits over it with transparent glyphs and its own caret, so
/// everything a textarea gives for free — selection, undo, spellcheck off, the accessible name its
/// label carries — is still a textarea's, and nothing here reimplements an editor.
///
/// The `<pre>` is what sizes the box, so the two never need their scroll positions kept in step:
/// they grow together and the surface around them does the scrolling.
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
      <div
        className={`relative h-[min(24rem,50vh)] min-w-0 overflow-auto rounded-md border bg-transparent focus-within:ring-[3px] focus-within:ring-ring/50 ${
          invalid ? "border-destructive focus-within:border-destructive" : "border-input focus-within:border-ring"
        }`}
      >
        {/* The painted copy sizes this box, and the textarea is positioned against it rather than
            against the scrolling frame, so the two scroll as one thing and nothing syncs them. */}
        <div className="relative min-h-full w-full">
          <pre
            aria-hidden="true"
            className="m-0 overflow-hidden px-3 py-2 font-mono text-sm break-words whitespace-pre-wrap"
          >
            {/* A document ending in a newline has no line box for that last line unless something
                follows it, so the painted copy runs one line longer than the text it mirrors. */}
            {highlightCode(value)}
            {"\n"}
          </pre>
          <textarea
            id={id}
            spellCheck={false}
            autoComplete="off"
            aria-invalid={invalid}
            value={value}
            onChange={(event) => onChange(event.target.value)}
            className="absolute inset-0 size-full resize-none overflow-hidden bg-transparent px-3 py-2 font-mono text-sm break-words whitespace-pre-wrap text-transparent caret-ink outline-none"
          />
        </div>
      </div>
    </div>
  );
}
