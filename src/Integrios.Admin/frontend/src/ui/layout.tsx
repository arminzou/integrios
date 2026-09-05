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

export function PageHeader({ title, children }: { title: ReactNode; children?: ReactNode }) {
  return (
    <header>
      <h1>{title}</h1>
      {children ? <p className="text-ink-secondary">{children}</p> : null}
    </header>
  );
}

/// A list in its own bordered card, with the caption naming the ordering above the rows rather than
/// below them. The table scrolls inside the card, so a wide list never makes the document scroll.
export function TableCard({ caption, children }: { caption: string; children: ReactNode }) {
  return (
    <Card className="gap-0 overflow-hidden py-0">
      <Table className="caption-top">
        <TableCaption className="mt-0 px-4 pt-4 pb-2 text-left">{caption}</TableCaption>
        {children}
      </Table>
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

/// The definition list every detail screen uses for its stored state: labels in one column, values
/// in the other, wrapping to one column when there is no room for two.
export function Details({ children }: { children: ReactNode }) {
  return (
    <dl className="grid grid-cols-[minmax(0,12rem)_minmax(0,1fr)] gap-x-6 gap-y-3 [&>dd]:m-0 [&>dt]:m-0 [&>dt]:font-medium">
      {children}
    </dl>
  );
}
