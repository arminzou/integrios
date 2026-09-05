import { cleanup, fireEvent, render, screen } from "@testing-library/react";
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

  it("returns focus to the trigger when confirmation is cancelled", () => {
    render(<ConfirmAction label="Deactivate" question="Deactivate this?" onConfirm={vi.fn()} />);
    const trigger = screen.getByRole("button", { name: "Deactivate" });
    fireEvent.click(trigger);
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Deactivate" }));

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Deactivate" }));
  });
});
