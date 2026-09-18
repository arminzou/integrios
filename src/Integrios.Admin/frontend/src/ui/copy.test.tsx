import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { BodyPanel } from "./copy";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function clipboard(writeText: (text: string) => Promise<void>) {
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
}

describe("Copying a shown value", () => {
  it("says it copied on the button, then clears", async () => {
    vi.useFakeTimers();
    const writeText = vi.fn().mockResolvedValue(undefined);
    clipboard(writeText);
    render(<BodyPanel label="Key" value="intg_abc.secret" language="text" />);

    const button = screen.getByRole("button", { name: "Copy key" });
    await act(async () => fireEvent.click(button));
    expect(writeText).toHaveBeenCalledWith("intg_abc.secret");
    expect(button.textContent).toBe("Copied");
    expect(screen.getByRole("status").textContent).toBe("Key copied.");

    act(() => vi.advanceTimersByTime(2000));
    expect(button.textContent).toBe("Copy");
    expect(screen.getByRole("status").textContent).toBe("");
  });

  it("selects the value for a keyboard copy when the clipboard refuses", async () => {
    clipboard(() => Promise.reject(new Error("denied")));
    render(<BodyPanel label="Key" value="intg_abc.secret" language="text" />);

    const button = screen.getByRole("button", { name: "Copy key" });
    await act(async () => fireEvent.click(button));
    expect(button.textContent).toBe("Copy failed");
    expect(window.getSelection()?.toString()).toContain("intg_abc.secret");
  });
});
