import { cn } from "cn";
import type * as React from "react";

/// The registry's Select is a Radix listbox. This one is a real `<select>`, because the platform
/// already has the element and it brings the mobile picker, type-ahead, and form behaviour with it;
/// a Radix primitive is for what the platform has no element for. Only the appearance is borrowed
/// from the registry's control, so a picker sits beside an `Input` without looking foreign.
function NativeSelect({ className, ...props }: React.ComponentProps<"select">) {
  return (
    <select
      data-slot="native-select"
      className={cn(
        "h-9 w-full min-w-0 rounded-md border border-input bg-transparent px-3 py-1 text-base transition-[color,box-shadow] outline-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm",
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50",
        "aria-invalid:border-destructive aria-invalid:ring-destructive/20",
        className,
      )}
      {...props}
    />
  );
}

export { NativeSelect };
