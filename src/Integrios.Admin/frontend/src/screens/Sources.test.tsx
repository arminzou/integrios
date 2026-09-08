import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { SourcesScreen } from "./Sources";

afterEach(cleanup);

it("shows the Source contract and restarts paging when the Topic filter changes", async () => {
  const tenantId = "11111111-1111-1111-1111-111111111111";
  const topicId = "22222222-2222-2222-2222-222222222222";
  const calls = stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, name: "orders", status: "disabled" }], "more-topics") };
    if (!url.pathname.endsWith("/sources")) return { status: 200, body: page([]) };
    const second = url.searchParams.has("after");
    return {
      status: 200,
      body: page(
        [
          {
            id: second ? "source-2" : "source-1",
            tenant_id: tenantId,
            topic_id: topicId,
            connection_id: "connection",
            type: "event_api",
            status: "active",
            source_contract: second ? "second_contract" : "order_json",
          },
        ],
        "cursor-1",
      ),
    };
  });
  const { router } = renderScreen(<SourcesScreen tenantId={tenantId} />, `/tenants/${tenantId}/sources`);
  await screen.findByText("order_json");
  expect(screen.getByRole("columnheader", { name: "Source contract" })).toBeTruthy();
  const topicFilter = screen.getByLabelText("Topic");
  expect(topicFilter.getAttribute("aria-describedby")).toBe("source-topic-hint");
  expect(document.getElementById("source-topic-hint")?.textContent).toBe("Showing the first 100 Topics.");
  fireEvent.click(screen.getByRole("button", { name: "Load more" }));
  await screen.findByText("second_contract");
  await act(() => router.navigate(`/tenants/${tenantId}/sources?topic_id=${topicId}`));
  await waitFor(() => {
    const latest = calls.filter(({ url }) => url.pathname.endsWith("/sources")).at(-1)!;
    expect(latest.url.searchParams.get("topic_id")).toBe(topicId);
    expect(latest.url.searchParams.has("after")).toBe(false);
  });
  expect(screen.queryByText("second_contract")).toBeNull();
  expect(screen.getByLabelText("Topic").textContent).toContain("orders");
});
