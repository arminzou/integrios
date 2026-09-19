import { cn } from "cn";
import { Popover as PopoverPrimitive } from "radix-ui";
import type * as React from "react";

/// A small panel anchored to the control that opened it, for a choice that needs more than a list
/// of options — the accepted range's presets beside two date inputs. It is a dialog that does not
/// take the page from the Operator: focus moves in on open and returns to the trigger on close, and
/// Escape closes, but nothing behind it is inert.
function Popover({ ...props }: React.ComponentProps<typeof PopoverPrimitive.Root>) {
  return <PopoverPrimitive.Root data-slot="popover" {...props} />;
}

function PopoverTrigger({ ...props }: React.ComponentProps<typeof PopoverPrimitive.Trigger>) {
  return <PopoverPrimitive.Trigger data-slot="popover-trigger" {...props} />;
}

function PopoverContent({ className, ...props }: React.ComponentProps<typeof PopoverPrimitive.Content>) {
  return (
    <PopoverPrimitive.Portal>
      <PopoverPrimitive.Content
        data-slot="popover-content"
        align="start"
        sideOffset={4}
        collisionPadding={8}
        className={cn(
          "z-50 flex w-72 max-w-(--radix-popover-content-available-width) flex-col gap-3 rounded-md border bg-surface p-3 text-sm shadow-[0_8px_24px_-16px_rgb(23_23_23/0.4)]",
          className,
        )}
        {...props}
      />
    </PopoverPrimitive.Portal>
  );
}

export { Popover, PopoverContent, PopoverTrigger };
