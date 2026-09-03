import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { useCursorList, type FetchResult } from "./useCursorList";

afterEach(cleanup);

type Page = { items: string[]; next_cursor: string | null };

describe("useCursorList", () => {
  it("does not let a stale request overwrite a newer scope", async () => {
    let resolveFirst!: (result: FetchResult<Page>) => void;
    const first = new Promise<FetchResult<Page>>((resolve) => {
      resolveFirst = resolve;
    });
    const load = vi
      .fn<(after: string | null) => Promise<FetchResult<Page>>>()
      .mockReturnValueOnce(first)
      .mockResolvedValueOnce({ data: { items: ["new"], next_cursor: null }, response: { status: 200 } });

    const { result, rerender } = renderHook(({ scope }) => useCursorList(load, scope), {
      initialProps: { scope: "old" },
    });
    await waitFor(() => expect(load).toHaveBeenCalledTimes(1));

    rerender({ scope: "new" });
    await waitFor(() => expect(result.current.items).toEqual(["new"]));

    await act(async () => {
      resolveFirst({ data: { items: ["old"], next_cursor: null }, response: { status: 200 } });
    });
    expect(result.current.items).toEqual(["new"]);
  });
});
