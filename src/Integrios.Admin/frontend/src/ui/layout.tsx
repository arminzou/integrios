import { cn } from "cn";
import { Slot } from "radix-ui";
import type { ComponentProps, ReactNode } from "react";
import { Link } from "react-router";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Table, TableCaption, TableHead } from "@/components/ui/table";

/// The page chrome every capability repeats: a title, an optional line saying where the page sits,
/// and the bounded groups beneath it. Only the shape is shared — what a page is about stays in the
/// screen that owns it.
export function Page({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("flex flex-col gap-5", className)}>{children}</div>;
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
        {children ? <p className="m-0 max-w-[64ch] text-ink-secondary">{children}</p> : null}
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
        <TableCaption className="mt-0 border-b px-4 py-3 text-left text-[13px]">{caption}</TableCaption>
        {children}
      </Table>
      {/* Paging belongs to the list it pages, not to the space under it. */}
      {footer ? (
        <div className="flex h-13 items-center justify-between gap-3 border-t px-4 text-[13px] text-ink-secondary">
          {footer}
        </div>
      ) : null}
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
export function Details({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <dl
      className={cn(
        "grid grid-cols-1 gap-x-3 gap-y-1.5 text-[13px] sm:grid-cols-[minmax(0,auto)_minmax(0,1fr)] [&>dd]:m-0 [&>dd]:break-words sm:[&>dd]:text-right [&>dt]:m-0 [&>dt]:text-ink-secondary",
        className,
      )}
    >
      {children}
    </dl>
  );
}

/// A list with the selected row's detail beside it where there is room for two columns, and
/// following it in document order where there is not. The Event ledger established the shape; every
/// capability whose detail is a detail rather than a workspace of its own uses it, so an Operator
/// compares rows and reads one in the same place.
///
/// Only the list and the detail are inside it. The page's title, its summary, and its filters
/// describe the whole screen rather than the ledger alone, so they run the full width above this;
/// pulling them into the list column would squeeze a one-line filter bar into four wrapped rows and
/// leave the inspector starting level with the page title instead of with the rows it explains.
export function SplitView({ children }: { children: ReactNode }) {
  return (
    <div className="flex flex-col gap-5 min-[1180px]:flex-row min-[1180px]:items-start min-[1180px]:gap-4">
      {children}
    </div>
  );
}

export function SplitList({ children }: { children: ReactNode }) {
  return <div className="min-w-0 min-[1180px]:flex-1">{children}</div>;
}

/// Sticky at desktop so the detail stays put while the list beside it is scanned. It carries its
/// own accessible name because it is a complementary region, not a second page.
export function Inspector({ label, className, children }: { label: string; className?: string; children: ReactNode }) {
  return (
    <aside
      aria-label={label}
      className={cn(
        "flex min-w-0 flex-col gap-3.5 rounded-lg border bg-card p-4 min-[1180px]:sticky min-[1180px]:top-4 min-[1180px]:w-100 min-[1180px]:flex-none",
        className,
      )}
    >
      {children}
    </aside>
  );
}

/// The column the inspector will occupy, held open while nothing is selected. Without it, choosing
/// the first row takes 400 pixels away from the ledger and reflows every column under the pointer
/// that was just used — and an Operator who has not yet clicked anything has no way to know the
/// detail panel is there at all. Desktop only: below the split it would be a box saying nothing
/// between the filters and the rows.
export function InspectorPlaceholder({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Inspector label={label} className="hidden min-[1180px]:flex">
      <p className="m-0 text-[13px] text-ink-secondary">{children}</p>
    </Inspector>
  );
}

/// The way back to an unselected list. The route is the selection, so closing the panel is a
/// navigation like opening it was — which keeps back, forward, and a copied link all meaning the
/// same thing they did before.
export function CloseInspector({ to, label }: { to: string; label: string }) {
  return (
    <Button asChild variant="ghost" size="icon-sm" title={label}>
      <Link to={to} aria-label={label}>
        <svg aria-hidden="true" viewBox="0 0 12 12" className="size-3">
          <path d="M3 3l6 6M9 3l-6 6" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
      </Link>
    </Button>
  );
}
