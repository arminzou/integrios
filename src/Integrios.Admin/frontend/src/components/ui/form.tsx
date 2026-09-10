import { cn } from "cn";
import type { Label as LabelPrimitive } from "radix-ui";
import { Slot } from "radix-ui";
import * as React from "react";
import {
  Controller,
  type ControllerProps,
  type FieldPath,
  type FieldValues,
  FormProvider,
  useFormContext,
  useFormState,
} from "react-hook-form";

import { Label } from "@/components/ui/label";

const Form = FormProvider;

type FormFieldContextValue<
  TFieldValues extends FieldValues = FieldValues,
  TName extends FieldPath<TFieldValues> = FieldPath<TFieldValues>,
> = {
  name: TName;
};

const FormFieldContext = React.createContext<FormFieldContextValue>({} as FormFieldContextValue);

const FormField = <
  TFieldValues extends FieldValues = FieldValues,
  TName extends FieldPath<TFieldValues> = FieldPath<TFieldValues>,
>({
  ...props
}: ControllerProps<TFieldValues, TName>) => {
  return (
    <FormFieldContext.Provider value={{ name: props.name }}>
      <Controller {...props} />
    </FormFieldContext.Provider>
  );
};

const useFormField = () => {
  const fieldContext = React.useContext(FormFieldContext);
  const itemContext = React.useContext(FormItemContext);
  const { getFieldState } = useFormContext();
  const formState = useFormState({ name: fieldContext.name });
  const fieldState = getFieldState(fieldContext.name, formState);

  if (!fieldContext) {
    throw new Error("useFormField should be used within <FormField>");
  }

  const { id } = itemContext;

  return {
    id,
    name: fieldContext.name,
    formItemId: `${id}-form-item`,
    formDescriptionId: `${id}-form-item-description`,
    formMessageId: `${id}-form-item-message`,
    ...fieldState,
  };
};

type FormItemContextValue = {
  id: string;
};

const FormItemContext = React.createContext<FormItemContextValue>({} as FormItemContextValue);

function FormItem({ className, ...props }: React.ComponentProps<"div">) {
  const id = React.useId();

  return (
    <FormItemContext.Provider value={{ id }}>
      {/* The row is what a field's floating message is positioned against. */}
      <div data-slot="form-item" className={cn("relative grid gap-2", className)} {...props} />
    </FormItemContext.Provider>
  );
}

function FormLabel({ className, ...props }: React.ComponentProps<typeof LabelPrimitive.Root>) {
  const { error, formItemId } = useFormField();

  return (
    <Label
      data-slot="form-label"
      data-error={!!error}
      className={cn("data-[error=true]:text-destructive", className)}
      htmlFor={formItemId}
      {...props}
    />
  );
}

function FormControl({ ...props }: React.ComponentProps<typeof Slot.Root>) {
  const { error, formItemId, formDescriptionId, formMessageId } = useFormField();

  return (
    <Slot.Root
      data-slot="form-control"
      id={formItemId}
      aria-describedby={error ? formMessageId : formDescriptionId}
      aria-invalid={!!error}
      {...props}
    />
  );
}

/// A field's standing guidance, read before the field is answered rather than after it is refused.
/// While a message is up it carries this text instead, and the line here keeps its box but stops
/// being drawn or announced — so the form does not move and the hint is not said twice.
function FormDescription({ className, ...props }: React.ComponentProps<"p">) {
  const { error, formDescriptionId } = useFormField();

  return (
    <p
      data-slot="form-description"
      id={formDescriptionId}
      // The base stylesheet hands a paragraph the margins prose wants; a hint is a line of a form
      // rather than prose, and the row's own gap is what spaces it from the control.
      className={cn("m-0 text-sm text-muted-foreground", error && "invisible", className)}
      {...props}
    />
  );
}

/// A message about one field, over the form rather than in it. In flow it pushed every control below
/// it down the page, so answering one field moved the others — on a form whose later sections appear
/// from what has been chosen, by several fields at a time.
///
/// It takes the shape of the browser's own validation bubble, in the console's palette: an Operator
/// has met that shape before, and it is the shape that reports a field without costing the form any
/// height. It carries the field's hint under the failure so the correction and the guidance are read
/// in one place. It takes no pointer events, and an ancestor that clipped its overflow would clip
/// it — the authoring surfaces scroll instead.
export function MessageBubble({
  id,
  role,
  message,
  hint,
  /// Where the message hangs from. "hint" is the field's own hint line, directly under the control,
  /// which is where a field's message belongs — the line is not being drawn while the message is up,
  /// so the message fills that space rather than leaving a hole and landing on the next field.
  /// "below" hangs under the whole box, for a message about a group rather than a control.
  placement = "below",
}: {
  id?: string;
  role?: "alert";
  message: React.ReactNode;
  hint?: React.ReactNode;
  placement?: "hint" | "below";
}) {
  return (
    <p
      id={id}
      role={role}
      className={cn(
        // The base stylesheet gives a paragraph its own margin, and the row gives the hint line a
        // gap: both would push the message away from the control it points at. A pixel above the
        // hint line puts the arrow's tip on the control's own edge.
        placement === "hint" ? "-top-px m-0" : "top-full mt-[7px]",
        "pointer-events-none absolute left-0 z-20 grid max-w-full grid-cols-[15px_1fr] gap-x-[7px] gap-y-[3px] rounded border border-danger-ink/35 bg-surface px-2.5 py-1.5 text-[12.5px] leading-[1.35] text-ink shadow-[0_3px_8px_rgb(23_23_23/0.14)] before:absolute before:-top-[5px] before:left-[13px] before:size-2 before:rotate-45 before:border-t before:border-l before:border-danger-ink/35 before:bg-surface before:content-['']",
      )}
    >
      <span
        aria-hidden="true"
        className="grid size-[15px] place-items-center rounded-[3px] bg-danger-ink text-[11px] leading-none font-bold text-surface"
      >
        !
      </span>
      <span>{message}</span>
      {hint ? <span className="col-start-2 text-xs text-ink-secondary">{hint}</span> : null}
    </p>
  );
}

function FormMessage({
  hint,
  placement,
  ...props
}: React.ComponentProps<"p"> & { hint?: React.ReactNode; placement?: "hint" | "below" }) {
  const { error, formMessageId } = useFormField();
  const body = error ? String(error?.message ?? "") : props.children;

  if (!body) {
    return null;
  }

  // A rejected field announces its message rather than only turning a colour.
  return error ? (
    <MessageBubble id={formMessageId} role="alert" message={body} hint={hint} placement={placement} />
  ) : (
    <p data-slot="form-message" id={formMessageId} className="text-sm text-destructive">
      {body}
    </p>
  );
}

export { Form, FormControl, FormDescription, FormField, FormItem, FormLabel, FormMessage, useFormField };
