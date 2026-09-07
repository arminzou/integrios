import { ChevronDown } from "lucide-react";
import { type ComponentProps, type ReactNode, useEffect, useState } from "react";
import type { Control, FieldValues, Path } from "react-hook-form";
import { Form, FormControl, FormDescription, FormField, FormItem, FormLabel, FormMessage } from "@/components/ui/form";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/native-select";
import { Textarea } from "@/components/ui/textarea";

/// One row of an authoring form: its label, its control, its hint, and whatever message the schema
/// or the Admin API attached to it. Every capability's fields are the same three shapes, so they are
/// written once here over the vendored `FormField`, which is still what a row expands to — a screen
/// that needs a control none of these wrap composes `FormField` directly.
type Row<TValues extends FieldValues> = {
  control: Control<TValues>;
  name: Path<TValues>;
  label: string;
  hint?: ReactNode;
};

export { Form };

export function TextField<TValues extends FieldValues>({
  control,
  name,
  label,
  hint,
  ...input
}: Row<TValues> & Omit<ComponentProps<typeof Input>, "name">) {
  return (
    <FormField
      control={control}
      name={name}
      render={({ field }) => (
        <FormItem>
          <FormLabel>{label}</FormLabel>
          <FormControl>
            <Input {...field} {...input} />
          </FormControl>
          {hint ? <FormDescription>{hint}</FormDescription> : null}
          <FormMessage />
        </FormItem>
      )}
    />
  );
}

export function TextAreaField<TValues extends FieldValues>({
  control,
  name,
  label,
  hint,
  ...textarea
}: Row<TValues> & Omit<ComponentProps<typeof Textarea>, "name">) {
  return (
    <FormField
      control={control}
      name={name}
      render={({ field }) => (
        <FormItem>
          <FormLabel>{label}</FormLabel>
          <FormControl>
            <Textarea {...field} {...textarea} />
          </FormControl>
          {hint ? <FormDescription>{hint}</FormDescription> : null}
          <FormMessage />
        </FormItem>
      )}
    />
  );
}

/// A picker over a fixed vocabulary or another capability's list. It stays a real `<select>`: the
/// platform already has the element, and it brings its own keyboard, mobile, and form behaviour.
export function SelectField<TValues extends FieldValues>({
  control,
  name,
  label,
  hint,
  children,
  ...select
}: Row<TValues> & Omit<ComponentProps<typeof NativeSelect>, "name">) {
  return (
    <FormField
      control={control}
      name={name}
      render={({ field }) => (
        <FormItem>
          <FormLabel>{label}</FormLabel>
          <FormControl>
            <NativeSelect {...field} {...select}>
              {children}
            </NativeSelect>
          </FormControl>
          {hint ? <FormDescription>{hint}</FormDescription> : null}
          <FormMessage />
        </FormItem>
      )}
    />
  );
}

/// A list filter, compact enough to sit in a permanently visible row above the list it scopes. It
/// carries its own current value, so a filtered list is legible as filtered without opening
/// anything, and an applied one is tinted as well as worded.
///
/// It stays a real `<select>` under a borrowed appearance: the platform element brings the keyboard,
/// the mobile picker, and the type-ahead, and only its own arrow is replaced so the control reads as
/// one pill rather than a label beside a box.
export const filterPill =
  "inline-flex h-9 max-w-full items-center gap-2 rounded-md border bg-surface px-2.5 text-sm whitespace-nowrap hover:bg-hover-surface data-[applied=true]:border-accent-border data-[applied=true]:bg-selected-surface data-[applied=true]:text-selected-ink";

// Capped so one long option — a Source named by its identifier — cannot stretch the pill across the
// bar. The select still opens at its natural width; only the closed control is bounded.
const filterControl =
  "max-w-44 min-w-0 cursor-pointer appearance-none overflow-hidden rounded-sm bg-transparent font-medium text-ellipsis outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50";

/// Drawn rather than borrowed from the font: the glyph this used to be (U+2304) lands on a
/// different baseline in every font it falls back through, and a caret that floats is the loudest
/// thing about an otherwise quiet control.
export function FilterCaret() {
  return <ChevronDown aria-hidden="true" focusable="false" className="size-3 shrink-0 text-ink-secondary" />;
}

/// A list filter, which belongs to the list rather than to a form: it re-reads from the first cursor
/// as soon as it changes, so there is nothing to submit and no schema to validate.
export function Filter({
  id,
  label,
  value,
  onChange,
  children,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  children: ReactNode;
}) {
  return (
    <span className={filterPill} data-applied={String(value !== "")}>
      <Label htmlFor={id} className="font-normal text-ink-secondary">
        {label}
      </Label>
      <select id={id} value={value} onChange={(event) => onChange(event.target.value)} className={filterControl}>
        {children}
      </select>
      <FilterCaret />
    </span>
  );
}

/// Finding a row by name, as the one free-text filter a list carries. It commits on Enter and on
/// blur rather than on every keystroke: each committed value is a different query with its own
/// pages, so typing a nine-character name straight into the URL would restart the cursor nine
/// times and issue eight reads nobody asked for.
export function FilterSearch({
  id,
  label,
  value,
  onChange,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
}) {
  const [typed, setTyped] = useState(value);

  // The URL can change without this control having produced it — Clear filters, the back button, a
  // pasted link — and the box follows it rather than keeping a value the list is not reading under.
  useEffect(() => setTyped(value), [value]);

  const commit = () => {
    if (typed.trim() !== value) onChange(typed.trim());
  };

  return (
    <form
      className={`${filterPill} min-w-52 flex-1`}
      data-applied={String(Boolean(value))}
      onSubmit={(event) => {
        event.preventDefault();
        commit();
      }}
    >
      <Label htmlFor={id} className="font-normal text-ink-secondary">
        {label}
      </Label>
      <input
        id={id}
        type="search"
        value={typed}
        placeholder="Any"
        onChange={(event) => setTyped(event.target.value)}
        onBlur={commit}
        className="min-w-0 appearance-none rounded-sm bg-transparent font-medium outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
      />
    </form>
  );
}

/// The same pill over a form field, for the one list whose filters are applied on submit rather than
/// on change. The hint a full-height field would print under the control becomes the pill's title
/// and its accessible description, so the row stays one line tall without losing the sentence.
export function FilterSelectField<TValues extends FieldValues>({
  control,
  name,
  label,
  hint,
  children,
  ...select
}: Row<TValues> & Omit<ComponentProps<"select">, "name">) {
  return (
    <FormField
      control={control}
      name={name}
      render={({ field }) => (
        <label className={filterPill} data-applied={String(Boolean(field.value))} title={hintText(hint)}>
          <span className="truncate text-ink-secondary">{label}</span>
          <select {...field} {...select} aria-label={label} className={filterControl}>
            {children}
          </select>
          <FilterCaret />
        </label>
      )}
    />
  );
}

export function FilterTextField<TValues extends FieldValues>({
  control,
  name,
  label,
  hint,
  ...input
}: Row<TValues> & Omit<ComponentProps<"input">, "name">) {
  return (
    <FormField
      control={control}
      name={name}
      render={({ field }) => (
        <label className={filterPill} data-applied={String(Boolean(field.value))} title={hintText(hint)}>
          <span className="truncate text-ink-secondary">{label}</span>
          <input
            {...field}
            {...input}
            aria-label={label}
            className="min-w-0 appearance-none rounded-sm bg-transparent font-medium outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
          />
        </label>
      )}
    />
  );
}

function hintText(hint: ReactNode): string | undefined {
  return typeof hint === "string" ? hint : undefined;
}
