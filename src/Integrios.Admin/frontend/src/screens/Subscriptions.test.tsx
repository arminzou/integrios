import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { SubscriptionsScreen } from "./Subscriptions";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const destinationId = "33333333-3333-3333-3333-333333333333";
const subscriptionId = "44444444-4444-4444-4444-444444444444";
const sourceId = "55555555-5555-5555-5555-555555555555";

const subscription = {
  id: subscriptionId,
  tenant_id: tenantId,
  topic_id: topicId,
  name: "Send priority orders",
  match_rules: { event_type: "order.placed" },
  destination_id: destinationId,
  mapping_config: { engine: "jsonata", version: "1", expression: '{ "customer": customer.id }' },
  http_delivery: { version: 1, method: "POST", path: null, headers: {}, body: "json" },
  http_success: null,
  status: "active",
  order_index: 1,
  description: null,
  created_at: "2026-09-08T00:00:00Z",
  updated_at: "2026-09-08T00:00:00Z",
};

function stubSubscriptionSources(items: unknown[], detail = subscription, nextItems?: unknown[]) {
  return stubHttp(({ url }) => {
    if (url.pathname.endsWith(`/subscriptions/${subscriptionId}`)) return { status: 200, body: detail };
    if (url.pathname.endsWith("/sources"))
      return {
        status: 200,
        body: url.searchParams.has("after") ? page(nextItems ?? []) : page(items, nextItems ? "next" : null),
      };
    if (url.pathname.endsWith("/topics")) return { status: 200, body: page([]) };
    if (url.pathname.endsWith("/destinations")) return { status: 200, body: page([]) };
    if (url.pathname.endsWith("/subscriptions")) return { status: 200, body: page([]) };
    return { status: 404, body: {} };
  });
}

it("lists Tenant Subscriptions with their Topic and destination names and sends every filter", async () => {
  const calls = stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, key: "orders", name: "Orders", status: "active" }]) };
    if (url.pathname.endsWith("/destinations"))
      return { status: 200, body: page([{ id: destinationId, name: "Primary CRM", status: "active" }]) };
    return {
      status: 200,
      body: page([
        {
          id: subscriptionId,
          tenant_id: tenantId,
          topic_id: topicId,
          topic_name: "Orders",
          destination_id: destinationId,
          destination_name: "Primary CRM",
          name: "Send priority orders",
          status: "active",
          order_index: 1,
          description: null,
          created_at: "2026-09-08T00:00:00Z",
          updated_at: "2026-09-08T00:00:00Z",
        },
      ]),
    };
  });

  renderScreen(
    <SubscriptionsScreen tenantId={tenantId} />,
    `/tenants/${tenantId}/subscriptions?name=priority&topic_id=${topicId}&destination_id=${destinationId}&status=active`,
  );

  expect(await screen.findByRole("link", { name: "Send priority orders" })).toBeTruthy();
  expect(screen.getByRole("link", { name: "Orders" }).getAttribute("href")).toBe(
    `/tenants/${tenantId}/topics/${topicId}`,
  );
  expect(screen.getByRole("link", { name: "Primary CRM" }).getAttribute("href")).toBe(
    `/tenants/${tenantId}/destinations/${destinationId}`,
  );
  expect(screen.getByRole("columnheader", { name: "Destination" })).toBeTruthy();
  await waitFor(() => {
    const list = calls.find(({ url }) => url.pathname.endsWith("/subscriptions"));
    expect(Object.fromEntries(list!.url.searchParams)).toMatchObject({
      name: "priority",
      topic_id: topicId,
      destination_id: destinationId,
      status: "active",
    });
  });
});

it("loads eligible Destinations for the selected Topic in a new Subscription", async () => {
  const calls = stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, key: "orders", name: "Orders", status: "active" }]) };
    if (url.pathname.endsWith("/destinations"))
      return { status: 200, body: page([{ id: destinationId, name: "Primary CRM", status: "active" }]) };
    return { status: 200, body: page([]) };
  });

  renderScreen(<SubscriptionsScreen tenantId={tenantId} />, `/tenants/${tenantId}/subscriptions?topic_id=${topicId}`);

  fireEvent.click(await screen.findByRole("button", { name: "New Subscription" }));
  const sheet = await screen.findByRole("dialog", { name: "New Subscription" });
  const destination = await within(sheet).findByRole("combobox", { name: "Destination" });
  await waitFor(() => expect(destination.hasAttribute("disabled")).toBe(false));
  expect(screen.queryByText("Not Found")).toBeNull();
  expect(calls.some(({ url }) => url.pathname.endsWith("/destinations"))).toBe(true);
});

it("explains when no active Source reaches the Subscription", async () => {
  stubSubscriptionSources([]);

  const { router } = renderScreen(
    <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
    `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`,
  );

  expect(await screen.findByText("No active Source publishes to this Topic.", { exact: false })).toBeTruthy();
  expect(screen.getByText("order.placed")).toBeTruthy();
  screen.getByRole("link", { name: "Create a Source" }).click();
  await waitFor(() => expect(router.state.location.pathname).toBe(`/tenants/${tenantId}/sources`));
  expect(router.state.location).toMatchObject({
    search: `?topic_id=${topicId}`,
    state: { openSourceCreate: true },
  });
});

it("opens the sole Source guide with simple mapping context", async () => {
  stubSubscriptionSources([
    {
      id: sourceId,
      tenant_id: tenantId,
      connector_id: "66666666-6666-6666-6666-666666666666",
      topic_id: topicId,
      name: "orders-intake",
      type: "event_api",
      status: "active",
      input_requirements: "",
    },
  ]);
  const { router } = renderScreen(
    <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
    `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`,
  );

  (await screen.findByRole("link", { name: /orders-intake/ })).click();
  await waitFor(() => expect(router.state.location.pathname).toBe(`/tenants/${tenantId}/sources/${sourceId}`));
  expect(router.state.location.state).toMatchObject({
    openSourceGuide: sourceId,
    sourceGuideContext: { eventType: "order.placed", payload: { customer: { id: null } }, advancedMapping: false },
  });
});

it("distinguishes multiple Sources and marks advanced mapping context", async () => {
  const secondSourceId = "66666666-6666-6666-6666-666666666666";
  stubSubscriptionSources(
    [
      {
        id: sourceId,
        tenant_id: tenantId,
        connector_id: "66666666-6666-6666-6666-666666666666",
        topic_id: topicId,
        name: "orders-intake",
        type: "event_api",
        status: "active",
        input_requirements: "",
      },
      ...Array.from({ length: 99 }, (_, index) => ({
        id: `source-${index}`,
        tenant_id: tenantId,
        connector_id: "66666666-6666-6666-6666-666666666666",
        topic_id: topicId,
        name: `bulk-intake-${index}`,
        type: "event_api",
        status: "active",
        input_requirements: "",
      })),
    ],
    { ...subscription, mapping_config: { engine: "jsonata", version: "1", expression: "$merge(payload)" } },
    [
      {
        id: secondSourceId,
        tenant_id: tenantId,
        connector_id: "66666666-6666-6666-6666-666666666666",
        topic_id: topicId,
        name: "webhook-intake",
        type: "webhook",
        status: "active",
        input_requirements: "",
      },
    ],
  );
  const { router } = renderScreen(
    <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
    `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`,
  );

  expect(await screen.findByText("Choose an upstream Source:")).toBeTruthy();
  expect(screen.getByRole("link", { name: /orders-intake/ })).toBeTruthy();
  const second = screen.getByRole("link", { name: /webhook-intake/ });
  expect(second.textContent).toContain("webhook · no requirements");
  second.click();
  await waitFor(() => expect(router.state.location.pathname).toBe(`/tenants/${tenantId}/sources/${secondSourceId}`));
  expect(router.state.location.state).toMatchObject({
    sourceGuideContext: { payload: {}, advancedMapping: true },
  });
});

it("authors the optional HTTP success rule on the Subscription", async () => {
  const calls = stubHttp(({ method, url }) => {
    if (url.pathname.endsWith(`/subscriptions/${subscriptionId}`)) return { status: 200, body: subscription };
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, key: "orders", name: "Orders", status: "active" }]) };
    if (url.pathname.endsWith("/destinations"))
      return { status: 200, body: page([{ id: destinationId, name: "CRM", status: "active" }]) };
    if (method === "PUT") return { status: 200, body: subscription };
    return { status: 200, body: page([]) };
  });
  renderScreen(
    <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
    `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`,
  );
  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  const form = await screen.findByRole("form", { name: `Edit ${subscription.name}` });
  fireEvent.change(within(form).getByLabelText("HTTP success rule (JSON, optional)"), {
    target: { value: '{"evaluator":"json_boolean","field":"ok","expected":true}' },
  });
  fireEvent.submit(form);
  await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));
  expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({
    http_success: { evaluator: "json_boolean", field: "ok", expected: true },
  });
});

describe("Editing a Subscription", () => {
  const detailPath = `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`;

  function stubEdit(detail: object, put: { status: number; body?: unknown } = { status: 200, body: detail }) {
    return stubHttp(({ method, url }) => {
      if (method === "PUT") return put;
      if (url.pathname.endsWith(`/subscriptions/${subscriptionId}`)) return { status: 200, body: detail };
      if (url.pathname.endsWith("/topics"))
        return { status: 200, body: page([{ id: topicId, key: "orders", name: "Orders", status: "active" }]) };
      if (url.pathname.endsWith("/destinations"))
        return { status: 200, body: page([{ id: destinationId, name: "CRM", status: "active" }]) };
      return { status: 200, body: page([]) };
    });
  }

  async function submitUnchanged() {
    renderScreen(
      <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
      detailPath,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    const form = await screen.findByRole("form", { name: `Edit ${subscription.name}` });
    fireEvent.submit(form);
    return form;
  }

  it.each([
    ["no success rule", null],
    ["a success rule", { evaluator: "json_boolean", field: "ok", expected: true }],
  ])("sends one with %s back exactly as it was read", async (_case, httpSuccess) => {
    const calls = stubEdit({ ...subscription, http_success: httpSuccess });
    await submitUnchanged();

    await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));
    // An update replaces the whole Subscription: a field the form drops, or an untouched optional
    // field sent as empty text, changes what the Subscription delivers.
    expect(calls.find((call) => call.method === "PUT")!.body).toEqual({
      name: subscription.name,
      match_rules: subscription.match_rules,
      destination_id: destinationId,
      mapping: subscription.mapping_config,
      http_delivery: subscription.http_delivery,
      http_success: httpSuccess,
      order_index: subscription.order_index,
      description: null,
    });
  });

  it("puts a refused match rule on the Event type field", async () => {
    stubEdit(subscription, { status: 422, body: { errors: { match_rules: ["Event type is not routable."] } } });
    const form = await submitUnchanged();

    const eventType = within(form).getByLabelText("Event type");
    await waitFor(() => expect(eventType.getAttribute("aria-invalid")).toBe("true"));
    const described = (eventType.getAttribute("aria-describedby") ?? "")
      .split(" ")
      .map((id) => document.getElementById(id)?.textContent ?? "")
      .join(" ");
    expect(described).toContain("Event type is not routable.");
  });

  it("offers no deactivation once it is disabled", async () => {
    stubEdit({ ...subscription, status: "disabled" });
    renderScreen(
      <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
      detailPath,
    );

    const panel = await screen.findByRole("complementary", { name: "Subscription detail" });
    await within(panel).findByRole("heading", { name: subscription.name });
    expect(within(panel).queryByRole("button", { name: /Deactivate/ })).toBeNull();
  });
});
