import type { ComponentProps, ReactNode } from "react";
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
    <div className="flex max-w-56 flex-col gap-2">
      <Label htmlFor={id}>{label}</Label>
      <NativeSelect id={id} value={value} onChange={(event) => onChange(event.target.value)}>
        {children}
      </NativeSelect>
    </div>
  );
}
