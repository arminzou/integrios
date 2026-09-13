import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { TopicsScreen } from "./Topics";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";

const topic = {
  id: topicId,
  tenant_id: tenantId,
  key: "order-events",
  name: "Order events",
  status: "active",
  description: null,
  subscription_count: 0,
  created_at: "2026-09-01T00:00:00Z",
  updated_at: "2026-09-01T00:00:00Z",
};

function stubTopics(detail = topic) {
  return stubHttp(({ url }) => {
    if (url.pathname.endsWith(`/topics/${topicId}`)) return { status: 200, body: detail };
    if (url.pathname.endsWith("/topics")) return { status: 200, body: page([detail]) };
    return { status: 200, body: page([]) };
  });
}

it("names each Topic by its key, with the label beside it", async () => {
  stubTopics();

  renderScreen(<TopicsScreen tenantId={tenantId} />, `/tenants/${tenantId}/topics`);

  // The key is what anything outside the dashboard refers to, so it heads the row.
  const row = await screen.findByRole("link", { name: "order-events" });
  expect(row.getAttribute("href")).toBe(`/tenants/${tenantId}/topics/${topicId}`);
  expect(screen.getByRole("columnheader", { name: "Key" })).toBeTruthy();
  expect(screen.getByRole("cell", { name: "Order events" })).toBeTruthy();
});

it("authors a key and leaves the label to the server when it is untouched", async () => {
  const calls = stubTopics();
  renderScreen(<TopicsScreen tenantId={tenantId} />, `/tenants/${tenantId}/topics`);

  fireEvent.click(await screen.findByRole("button", { name: "New Topic" }));
  const form = await screen.findByRole("form", { name: "Create a Topic" });
  fireEvent.change(within(form).getByLabelText("Key"), { target: { value: "order-events" } });
  fireEvent.submit(form);

  await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
  expect(calls.find((call) => call.method === "POST")!.body).toEqual({
    key: "order-events",
    name: null,
    description: null,
  });
});

it("refuses a key that is not a lowercase label before sending it", async () => {
  const calls = stubTopics();
  renderScreen(<TopicsScreen tenantId={tenantId} />, `/tenants/${tenantId}/topics`);

  fireEvent.click(await screen.findByRole("button", { name: "New Topic" }));
  const form = await screen.findByRole("form", { name: "Create a Topic" });
  fireEvent.change(within(form).getByLabelText("Key"), { target: { value: "Order Events" } });
  fireEvent.submit(form);

  const key = within(form).getByLabelText("Key");
  await waitFor(() => expect(key.getAttribute("aria-invalid")).toBe("true"));
  expect(calls.some((call) => call.method === "POST")).toBe(false);
});

it("edits the label and offers no way to change the key", async () => {
  const calls = stubTopics();
  renderScreen(
    <TopicsScreen tenantId={tenantId} selectedTopicId={topicId} />,
    `/tenants/${tenantId}/topics/${topicId}`,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  const form = await screen.findByRole("form", { name: `Edit ${topic.name}` });
  // The key is immutable, so the edit form states it rather than offering it.
  expect(within(form).queryByLabelText("Key")).toBeNull();
  expect(within(form).getByText("order-events")).toBeTruthy();

  fireEvent.change(within(form).getByLabelText("Name"), { target: { value: "Orders" } });
  fireEvent.submit(form);

  await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));
  expect(calls.find((call) => call.method === "PUT")!.body).toEqual({ name: "Orders", description: null });
});
