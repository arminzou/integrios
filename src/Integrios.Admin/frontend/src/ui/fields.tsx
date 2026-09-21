import { cn } from "cn";
import { Children, type ComponentProps, isValidElement, type ReactNode, useState } from "react";
import type { Control, FieldValues, Path } from "react-hook-form";
import { Form, FormControl, FormDescription, FormField, FormItem, FormLabel, FormMessage } from "@/components/ui/form";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { type CodeLanguage, CodeTextarea } from "./codeHighlight";

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

/// A field's hint line, and the message that takes its place. The message hangs from this line
/// rather than from the row, so it sits directly under the control it is about; the line keeps its
/// box while a message is up, so the row's height never changes. A field with no hint has no line to
/// hang from, and the message hangs from the row instead — which is the same place, there being
/// nothing after the control.
function FieldFooter({ hint }: { hint?: ReactNode }) {
  return hint ? (
    <div className="relative">
      <FormDescription>{hint}</FormDescription>
      <FormMessage hint={hint} placement="hint" />
    </div>
  ) : (
    <FormMessage />
  );
}

export function TextField<TValues extends FieldValues>({
  control,
  name,
  label,
  hint,
  onChange,
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
            {/* A screen that wants to hear a keystroke gets it alongside the form's own binding
                rather than in place of it: replacing `onChange` would silently unbind the field. */}
            <Input
              {...field}
              {...input}
              onChange={(event) => {
                field.onChange(event);
                onChange?.(event);
              }}
            />
          </FormControl>
          <FieldFooter hint={hint} />
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
  language,
  ...textarea
}: Row<TValues> &
  Omit<ComponentProps<typeof Textarea>, "name" | "value" | "defaultValue"> & {
    /// Set for a code field: it is edited in a `CodeTextarea` and painted in this grammar.
    language?: CodeLanguage;
  }) {
  return (
    <FormField
      control={control}
      name={name}
      render={({ field }) => (
        <FormItem>
          <FormLabel>{label}</FormLabel>
          <FormControl>
            {language ? (
              <CodeTextarea {...field} {...textarea} language={language} />
            ) : (
              <Textarea {...field} {...textarea} />
            )}
          </FormControl>
          <FieldFooter hint={hint} />
        </FormItem>
      )}
    />
  );
}

/// Radix reads the empty string as "nothing is selected", so a value that genuinely means empty
/// cannot be one. Filters translate at their own boundary and screens never write the sentinel.
const EMPTY = "\u0000empty";
const toControl = (value: string) => (value === "" ? EMPTY : value);
const fromControl = (value: string) => (value === EMPTY ? "" : value);

/// A picker over a fixed vocabulary or another capability's list. An unset field shows the
/// placeholder rather than an option that has to be authored on every screen that has one.
export function SelectField<TValues extends FieldValues>({
  control,
  name,
  label,
  hint,
  placeholder,
  emptyLabel,
  disabled,
  required,
  onChange,
  children,
}: Row<TValues> & {
  placeholder?: string;
  /// Names the empty value as a choice, for a field that is genuinely optional. Without it the
  /// first selection is final: the placeholder is not an option, so there is nothing to pick to get
  /// back to unset, and an Operator who chose by mistake has to abandon the form. `Filter` offers
  /// the same thing as "All"; a form says what its own empty case means.
  emptyLabel?: string;
  disabled?: boolean;
  /// The trigger is a button, so it cannot carry the native attribute; the schema is what refuses an
  /// empty value, and this is what says so before the refusal.
  required?: boolean;
  /// Heard alongside the form's own binding rather than in place of it, as `TextField` does. A field
  /// whose offered options or whose companion field depend on this one resets them from here: the
  /// dependency is on the act of choosing, which an effect watching the value can only infer.
  onChange?: (value: string) => void;
  children: ReactNode;
}) {
  return (
    <FormField
      control={control}
      name={name}
      render={({ field }) => (
        <FormItem>
          <FormLabel>{label}</FormLabel>
          <Select
            value={emptyLabel ? toControl(field.value ?? "") : (field.value ?? "")}
            onValueChange={(value) => {
              field.onChange(fromControl(value));
              onChange?.(fromControl(value));
            }}
            disabled={disabled}
          >
            <FormControl>
              <SelectTrigger onBlur={field.onBlur} name={field.name} aria-required={required}>
                <SelectValue placeholder={placeholder ?? "Choose one"} />
              </SelectTrigger>
            </FormControl>
            <SelectContent>
              {emptyLabel ? <SelectItem value={EMPTY}>{emptyLabel}</SelectItem> : null}
              {children}
            </SelectContent>
          </Select>
          <FieldFooter hint={hint} />
        </FormItem>
      )}
    />
  );
}

/// The pill treatment the list filters share: compact enough to sit in a permanently visible row
/// above the list it scopes, carrying the control's current value so a filtered list is legible as
/// filtered without opening anything, and tinted as well as worded when applied. The control inside
/// supplies the behaviour — a vendored Radix trigger or a native input — so the pill supplies only
/// the shape.
export const filterPill =
  "inline-flex h-9 max-w-full items-center gap-2 rounded-md border border-input bg-surface px-2.5 text-sm whitespace-nowrap hover:bg-hover-surface data-[applied=true]:border-accent-border data-[applied=true]:bg-selected-surface data-[applied=true]:text-selected-ink";

const searchFilterPill = (wide?: boolean) =>
  cn(
    filterPill,
    "group relative w-full focus-within:border-accent-border focus-within:bg-selected-surface focus-within:text-selected-ink",
    // A pill with a leading control gives that control the padding, so it can reach the pill's edge.
    wide ? "max-w-[26rem] pl-0" : "max-w-64",
  );

/// A list filter, which belongs to the list rather than to a form: it re-reads from the first cursor
/// as soon as it changes, so there is nothing to submit and no schema to validate. It renders the
/// vendored Radix listbox, not a native `<select>`, so the menu is the product's own on every
/// platform, phones included; only the trigger borrows the pill's shape.
///
/// "All" is the filter's own empty case rather than an option each screen has to remember to write,
/// which is also what keeps the sentinel Radix needs out of every call site. It is named in the menu
/// only: an unset pill shows just its label, and the value appears once one is applied.
/// `hint` says what the offered options do not cover. A filter built from a read of its own is
/// capped the way every picker here is, so a Topic past the first hundred is a row the list can
/// show but this control cannot select. The trigger describes that limit for assistive technology,
/// and the open menu states it visibly.
export function Filter({
  id,
  label,
  value,
  onChange,
  hint,
  disabled,
  children,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  hint?: string;
  disabled?: boolean;
  children: ReactNode;
}) {
  const hintId = hint ? `${id}-hint` : undefined;
  // A value the options cannot name — past the capped read, or deleted — is still in force, so it
  // is shown as the selection rather than as a tinted pill with no value.
  const unnamed =
    value !== "" &&
    !Children.toArray(children).some(
      (child) => isValidElement<{ value?: string }>(child) && child.props.value === value,
    );

  return (
    <Select value={toControl(value)} onValueChange={(next) => onChange(fromControl(next))} disabled={disabled}>
      <SelectTrigger
        id={id}
        aria-label={label}
        aria-describedby={hintId}
        data-applied={String(value !== "")}
        className={`${filterPill} w-auto justify-start`}
      >
        <span className="font-normal text-ink-secondary">{label}</span>
        {/* An unset filter shows its label alone; the value appears once one is applied. */}
        {value !== "" ? (
          <span className="min-w-0 truncate font-medium">
            <SelectValue />
          </span>
        ) : null}
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={EMPTY}>All</SelectItem>
        {unnamed ? <SelectItem value={value}>{value}</SelectItem> : null}
        {children}
        {hint ? (
          <p aria-hidden="true" className="border-t border-accent-border px-2 py-1.5 text-xs text-ink-secondary">
            {hint}
          </p>
        ) : null}
      </SelectContent>
      {hint ? (
        <span id={hintId} className="sr-only">
          {hint}
        </span>
      ) : null}
    </Select>
  );
}

/// Finding a row by name, as the one free-text filter a list carries. It commits on Enter and on
/// blur rather than on every keystroke: each committed value is a different query with its own
/// pages, so typing a nine-character name straight into the URL would restart the cursor nine
/// times and issue eight reads nobody asked for.
/// Which field a search pill is matching, chosen inside the pill itself. A list whose one free-text
/// filter could mean either of two fields offers this rather than a box per field: two boxes are
/// ANDed, so they can be set to identities belonging to different rows, and the list then comes back
/// empty with nothing saying why. One box and this chooser cannot express that.
///
/// It is muted and reads as part of the box, not as a filter of its own: the field is not scope, the
/// value in the box is.
export function SearchFieldChooser({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  children: ReactNode;
}) {
  return (
    <Select value={value} onValueChange={onChange}>
      {/* The trigger fills the zone it sits in rather than floating inside the pill, so the menu
          Radix anchors to it opens where every other filter's menu opens: at the pill's leading
          edge, below the pill. Anchoring to a button tucked inside the pill is what put this menu a
          few pixels high and inset, and no offset can fix that for every width. */}
      <SelectTrigger unstyled aria-label={label} className="h-full px-2.5 font-medium text-ink-secondary">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>{children}</SelectContent>
    </Select>
  );
}

/// `leading` puts a control inside the pill, in front of the box: the Event ledger chooses there
/// which identity it is searching. It stays a separate control with its own name — the pill's own
/// label belongs to the input and is bound to it by id, so no label names two controls at once.
///
/// `hint` says how the box matches. It is the box's accessible description, and the screen renders
/// the same sentence where it has room; see `FilterHint`.
export function FilterSearch({
  id,
  label,
  placeholder,
  value,
  onChange,
  hint,
  leading,
}: {
  id: string;
  /// The field the box matches; it is the box's accessible name.
  label: string;
  /// How it matches, e.g. `Name contains…`.
  placeholder?: string;
  value: string;
  onChange: (value: string) => void;
  hint?: string;
  leading?: ReactNode;
}) {
  const [typed, setTyped] = useState(value);
  const [seen, setSeen] = useState(value);

  // The URL can change without this control having produced it — Clear filters, the back button, a
  // pasted link — and the box follows it rather than keeping a value the list is not reading under.
  // Adjusted while rendering, not in an effect, so there is no frame showing the stale text.
  if (value !== seen) {
    setSeen(value);
    setTyped(value);
  }

  const commit = () => {
    if (typed.trim() !== value) onChange(typed.trim());
  };

  return (
    <form
      className={searchFilterPill(Boolean(leading))}
      data-applied={String(Boolean(value))}
      onSubmit={(event) => {
        event.preventDefault();
        commit();
      }}
    >
      <Label htmlFor={id} className="sr-only">
        {label}
      </Label>
      {/* The chooser is muted and divided from the box, so the pill reads as one filter whose field
          happens to be selectable rather than as two controls sharing a border. */}
      {leading ? (
        <span className="-my-px -ml-px flex h-[calc(100%+2px)] items-center border-r border-input text-ink-secondary">
          {leading}
        </span>
      ) : null}
      {/* A scope, not a detail about the Operator: the browser has nothing to offer from past forms,
          and a name, an id or an Event type is not prose to be spell-checked against a dictionary. */}
      <input
        id={id}
        type="search"
        value={typed}
        placeholder={placeholder ?? label}
        aria-describedby={hint ? `${id}-hint` : undefined}
        autoComplete="off"
        spellCheck={false}
        onChange={(event) => setTyped(event.target.value)}
        onBlur={commit}
        className="min-w-0 flex-1 appearance-none rounded-sm bg-transparent font-medium outline-none"
      />
      {/* How the box matches, shown beside it while the pill is hovered or holds focus — a keyboard
          and a touch screen reach it too — and always present as the box's own description, so it is
          announced whether or not it is being drawn. Out of flow, so the list below never moves. */}
      {hint ? (
        <span
          id={`${id}-hint`}
          className="pointer-events-none absolute top-full left-0 z-50 mt-1.5 hidden max-w-[29rem] rounded-md bg-ink px-2 py-1.5 text-xs font-normal text-surface whitespace-normal group-hover:block group-focus-within:block"
        >
          {hint}
        </span>
      ) : null}
    </form>
  );
}
