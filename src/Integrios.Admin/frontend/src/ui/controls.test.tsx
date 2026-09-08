import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConfirmAction, Field, fieldProps } from "./controls";

afterEach(cleanup);

describe("Shared controls", () => {
  it("associates a field with both its hint and error", () => {
    render(
      <Field id="name" label="Name" hint="Use a stable name." error="That name is taken.">
        <input {...fieldProps("name", "That name is taken.", true)} />
      </Field>,
    );

    expect(screen.getByLabelText("Name").getAttribute("aria-describedby")).toBe("name-hint name-error");
  });

  it("returns focus to the trigger when confirmation is cancelled", async () => {
    render(<ConfirmAction label="Deactivate" question="Deactivate this?" onConfirm={vi.fn()} />);
    const trigger = screen.getByRole("button", { name: "Deactivate" });
    fireEvent.click(trigger);
    const dialog = screen.getByRole("dialog", { name: "Deactivate" });
    expect(within(dialog).getByText("Deactivate this?")).toBeTruthy();
    expect(document.activeElement).toBe(within(dialog).getByRole("button", { name: "Cancel" }));

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("button", { name: "Deactivate" })));
  });
});
