import { cn } from "cn";
import { X } from "lucide-react";
import { Dialog as SheetPrimitive } from "radix-ui";
import type * as React from "react";

/// A panel that arrives from the trailing edge and sits over the content, rather than inside the
/// layout. It exists because the alternatives were measured and both moved the list: opening the
/// form above it pushed the filters, the rows and the open detail down the page, and rendering it
/// into the detail column meant widening that column to hold a configuration document, which took
/// the difference out of the list. Out of flow is the only placement that leaves a list where it was.
///
/// It is a dialog rather than a styled `<div>`, and behaves like one: focus moves in on open, is
/// held inside while it is open, and returns to whatever opened it on close. Escape closes. That
/// behaviour is the reason a primitive is vendored here at all — it is what the platform has no
/// element for, which is the only thing that justifies one.
function Sheet({ ...props }: React.ComponentProps<typeof SheetPrimitive.Root>) {
  return <SheetPrimitive.Root data-slot="sheet" {...props} />;
}

function SheetTrigger({ ...props }: React.ComponentProps<typeof SheetPrimitive.Trigger>) {
  return <SheetPrimitive.Trigger data-slot="sheet-trigger" {...props} />;
}

function SheetClose({ ...props }: React.ComponentProps<typeof SheetPrimitive.Close>) {
  return <SheetPrimitive.Close data-slot="sheet-close" {...props} />;
}

/// The scrim covers everything, navigation included. A surface that holds focus and answers Escape
/// is modal whether or not it looks it, so the page behind it is dimmed and inert rather than
/// half-available: an Operator who can see a nav item but cannot reach it has been told something
/// untrue about the page.
function SheetOverlay({ className, ...props }: React.ComponentProps<typeof SheetPrimitive.Overlay>) {
  return (
    <SheetPrimitive.Overlay
      data-slot="sheet-overlay"
      className={cn("fixed inset-0 z-40 bg-ink/20", className)}
      {...props}
    />
  );
}

function SheetContent({ className, children, ...props }: React.ComponentProps<typeof SheetPrimitive.Content>) {
  return (
    <SheetPrimitive.Portal>
      <SheetOverlay />
      <SheetPrimitive.Content
        data-slot="sheet-content"
        // Wide enough that a configuration document is legible while it is being typed, and a full
        // sheet below that width, where a fixed column would leave nothing for the form itself.
        className={cn(
          "fixed inset-y-0 right-0 z-50 flex w-full max-w-140 flex-col gap-4 overflow-y-auto border-l bg-surface p-4 shadow-[-16px_0_32px_-24px_rgb(23_23_23/0.45)]",
          className,
        )}
        {...props}
      >
        {children}
      </SheetPrimitive.Content>
    </SheetPrimitive.Portal>
  );
}

/// The title is required rather than optional: a dialog without an accessible name is announced as
/// "dialog" and nothing else.
function SheetHeader({ title, description }: { title: string; description?: string }) {
  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0">
        <SheetPrimitive.Title className="m-0">{title}</SheetPrimitive.Title>
        {description ? (
          <SheetPrimitive.Description className="m-0 mt-0.5 text-xs text-ink-secondary">
            {description}
          </SheetPrimitive.Description>
        ) : (
          <SheetPrimitive.Description className="sr-only">{title}</SheetPrimitive.Description>
        )}
      </div>
      <SheetPrimitive.Close
        aria-label={`Close ${title}`}
        className="-m-1 flex size-8 shrink-0 items-center justify-center rounded-md p-1 hover:bg-hover-surface focus-visible:ring-[3px] focus-visible:ring-ring/50"
      >
        <X aria-hidden="true" className="size-4" />
      </SheetPrimitive.Close>
    </div>
  );
}

export { Sheet, SheetClose, SheetContent, SheetHeader, SheetTrigger };
