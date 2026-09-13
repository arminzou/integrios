import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "cn";
import { Slot } from "radix-ui";
import type * as React from "react";

/// The press is a colour, stated per variant rather than once on the base, because it has to deepen
/// whatever that variant's hover already did: pressing continues the gesture hover started instead
/// of being a second unrelated effect. It is also the vocabulary every other interactive state here
/// already speaks — hover, focus ring, applied filter — so a transform would have been the only one
/// of its kind for one state.
///
/// The two outlined variants press from the hover surface down to the selected one, a real step in
/// the same palette. The filled ones press from their translucent hover back to full strength: over
/// a light canvas that reads as deepening, which is the move available on a near-black control where
/// "darker" is not.
const buttonVariants = cva(
  "inline-flex shrink-0 items-center justify-center gap-2 rounded-md text-sm font-medium whitespace-nowrap transition-all outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:pointer-events-none disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-destructive/20 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
  {
    variants: {
      variant: {
        default: "bg-primary text-primary-foreground hover:bg-primary/90 active:bg-primary",
        destructive:
          "bg-destructive text-white hover:bg-destructive/90 focus-visible:ring-destructive/20 active:bg-destructive",
        outline:
          "border border-input bg-background hover:bg-accent hover:text-accent-foreground active:bg-selected-surface",
        secondary: "bg-secondary text-secondary-foreground hover:bg-secondary/80 active:bg-secondary",
        ghost: "hover:bg-accent hover:text-accent-foreground active:bg-selected-surface",
        // A text link has no box to deepen, so its press dims the text the underline already marked.
        link: "text-primary underline-offset-4 hover:underline active:opacity-70",
      },
      size: {
        default: "h-9 px-4 py-2 has-[>svg]:px-3",
        xs: "h-6 gap-1 rounded-md px-2 text-xs has-[>svg]:px-1.5 [&_svg:not([class*='size-'])]:size-3",
        sm: "h-8 gap-1.5 rounded-md px-3 has-[>svg]:px-2.5",
        lg: "h-10 rounded-md px-6 has-[>svg]:px-4",
        icon: "size-9",
        "icon-xs": "size-6 rounded-md [&_svg:not([class*='size-'])]:size-3",
        "icon-sm": "size-8",
        "icon-lg": "size-10",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  },
);

function Button({
  className,
  variant = "default",
  size = "default",
  asChild = false,
  ...props
}: React.ComponentProps<"button"> &
  VariantProps<typeof buttonVariants> & {
    asChild?: boolean;
  }) {
  const Comp = asChild ? Slot.Root : "button";

  return (
    <Comp
      data-slot="button"
      data-variant={variant}
      data-size={size}
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    />
  );
}

export { Button, buttonVariants };
