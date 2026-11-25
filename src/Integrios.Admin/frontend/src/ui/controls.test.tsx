import { QueryClientProvider, useMutation } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useEffect, useState } from "react";
import { afterEach, describe, expect, it } from "vitest";
import { createQueryClient } from "../api/query";
import { CreateSheet, SheetButton } from "./controls";

afterEach(cleanup);

/// A screen that owns its create sheet and renders the trigger only while `withTrigger` holds — the
/// shape every list screen has, where the page header's action goes away as soon as the list answers
/// that it is empty and the card replacing the table takes the action over.
function Screen({ withTrigger }: { withTrigger: boolean }) {
  const [creating, setCreating] = useState(false);
  const [client] = useState(createQueryClient);
  return (
    <QueryClientProvider client={client}>
      {withTrigger ? <SheetButton label="New Thing" expanded={creating} onOpen={() => setCreating(true)} /> : null}
      <CreateSheet label="New Thing" open={creating} onOpenChange={setCreating}>
        {() => <input aria-label="Name" />}
      </CreateSheet>
    </QueryClientProvider>
  );
}

describe("A sheet whose open state the screen owns", () => {
  it("keeps a half-filled form when the control that opened it is taken off the page", async () => {
    const { rerender } = render(<Screen withTrigger={true} />);

    fireEvent.click(screen.getByRole("button", { name: "New Thing" }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "half typed" } });

    // What a list answering "empty" does to the page header: the trigger is gone, and the form an
    // Operator had already started must not go with it.
    rerender(<Screen withTrigger={false} />);

    expect(screen.queryByRole("button", { name: "New Thing" })).toBeNull();
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("half typed");
  });

  it("renders no trigger of its own, because the screen supplies them", () => {
    render(<Screen withTrigger={false} />);

    expect(screen.queryByRole("button", { name: "New Thing" })).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

/// A write that never settles, standing in for a stalled request elsewhere on the screen.
function StalledWrite({ holdsSheet }: { holdsSheet: boolean }) {
  const write = useMutation({ meta: { holdsSheet }, mutationFn: () => new Promise<never>(() => {}) });
  // biome-ignore lint/correctness/useExhaustiveDependencies: start the write once, on mount
  useEffect(() => write.mutate(), []);
  return null;
}

function SheetBesideAWrite({ holdsSheet }: { holdsSheet: boolean }) {
  const [client] = useState(createQueryClient);
  const [open, setOpen] = useState(true);
  return (
    <QueryClientProvider client={client}>
      <StalledWrite holdsSheet={holdsSheet} />
      <CreateSheet label="New Thing" open={open} onOpenChange={setOpen}>
        {() => <input aria-label="Name" />}
      </CreateSheet>
    </QueryClientProvider>
  );
}

describe("A sheet while a write is in flight", () => {
  it("still closes on Escape when the pending write is not one that holds it", async () => {
    render(<SheetBesideAWrite holdsSheet={false} />);

    fireEvent.keyDown(await screen.findByRole("dialog"), { key: "Escape" });

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("stays open on Escape while a write that holds it is pending", async () => {
    render(<SheetBesideAWrite holdsSheet={true} />);

    fireEvent.keyDown(await screen.findByRole("dialog"), { key: "Escape" });

    expect(screen.getByRole("dialog")).toBeTruthy();
  });
});
