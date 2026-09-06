import { cn } from "cn";
import { Slot } from "radix-ui";
import type { ComponentProps, ReactNode } from "react";
import { Card } from "@/components/ui/card";
import { Table, TableCaption, TableHead } from "@/components/ui/table";

/// The page chrome every capability repeats: a title, an optional line saying where the page sits,
/// and the bounded groups beneath it. Only the shape is shared — what a page is about stays in the
/// screen that owns it.
export function Page({ children }: { children: ReactNode }) {
  return <div className="flex flex-col gap-8">{children}</div>;
}

/// Title, one line saying where the page sits, and the page's own primary action at the trailing
/// edge, closed by a rule. The action is a slot rather than a prop pair so a screen hands over the
/// control it already owns — including its confirmation and pending states — instead of this shape
/// having to know what a capability's primary action is.
///
/// A screen with no primary action passes none, and the row is then just a title.
export function PageHeader({
  title,
  action,
  children,
}: {
  title: ReactNode;
  action?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <header className="flex flex-wrap items-start justify-between gap-x-6 gap-y-3 border-b pb-4">
      <div className="min-w-0">
        <h1>{title}</h1>
        {children ? <p className="m-0 text-ink-secondary">{children}</p> : null}
      </div>
      {action ? <div className="shrink-0">{action}</div> : null}
    </header>
  );
}

/// A list in its own bordered card, with the caption naming the ordering above the rows rather than
/// below them. The table scrolls inside the card, so a wide list never makes the document scroll.
export function TableCard({ caption, footer, children }: { caption: string; footer?: ReactNode; children: ReactNode }) {
  return (
    <Card className="gap-0 overflow-hidden py-0">
      <Table className="caption-top">
        <TableCaption className="mt-0 px-4 pt-4 pb-2 text-left">{caption}</TableCaption>
        {children}
      </Table>
      {/* Paging belongs to the list it pages, not to the space under it. */}
      {footer ? <div className="flex items-center justify-between gap-3 border-t px-4 py-2">{footer}</div> : null}
    </Card>
  );
}

/// The cell that names a row — a `<th scope="row">`, so a screen reader announces the row by its
/// identity, without the quiet uppercase treatment the column headers carry.
export function RowHeader({ className, ...props }: ComponentProps<typeof TableHead>) {
  return <TableHead scope="row" className={`font-normal whitespace-normal ${className ?? ""}`} {...props} />;
}

/// A form or a read-only group as a bounded card, the same box the lists sit in. `asChild` hands the
/// box to the element that already has a reason to exist — usually the `<form>` itself.
export function Panel({ className, asChild, ...props }: ComponentProps<"div"> & { asChild?: boolean }) {
  const Box = asChild ? Slot.Root : "div";
  return <Box className={cn("max-w-2xl rounded-lg border bg-card p-6 text-card-foreground", className)} {...props} />;
}

/// The definition list every detail screen uses for its stored state: labels beside their values
/// where there is room for two columns, and label above value where there is not. Two columns at
/// 320 CSS pixels would leave the value column narrower than the identifiers it has to hold, which
/// is what makes the document itself scroll sideways.
export function Details({ children }: { children: ReactNode }) {
  return (
    <dl className="grid grid-cols-1 gap-x-6 gap-y-3 sm:grid-cols-[minmax(0,12rem)_minmax(0,1fr)] [&>dd]:m-0 [&>dt]:m-0 [&>dt]:font-medium">
      {children}
    </dl>
  );
}
