import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it } from "vitest";
import { CreateSheet, SheetButton } from "./controls";

afterEach(cleanup);

/// A screen that owns its create sheet and renders the trigger only while `withTrigger` holds — the
/// shape every list screen has, where the page header's action goes away as soon as the list answers
/// that it is empty and the card replacing the table takes the action over.
function Screen({ withTrigger }: { withTrigger: boolean }) {
  const [creating, setCreating] = useState(false);
  return (
    <>
      {withTrigger ? <SheetButton label="New Thing" expanded={creating} onOpen={() => setCreating(true)} /> : null}
      <CreateSheet label="New Thing" open={creating} onOpenChange={setCreating}>
        {() => <input aria-label="Name" />}
      </CreateSheet>
    </>
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
