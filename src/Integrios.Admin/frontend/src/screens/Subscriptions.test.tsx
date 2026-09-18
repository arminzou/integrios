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
  event_types: ["order.placed"],
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

it("says what a Subscription is missing when the Tenant has no Topic and no Destination", async () => {
  stubHttp(() => ({ status: 200, body: page([]) }));

  renderScreen(<SubscriptionsScreen tenantId={tenantId} />, `/tenants/${tenantId}/subscriptions`);
  fireEvent.click(await screen.findByRole("button", { name: "New Subscription" }));
  const sheet = await screen.findByRole("dialog", { name: "New Subscription" });

  // A Subscription is the last thing a Tenant authors, so it is the form most able to be blocked:
  // it routes from a Topic to a Destination and needs both to exist first.
  const topicHint = await within(sheet).findByText(/No active Topics yet/);
  expect(within(topicHint).getByRole("link", { name: "Create a Topic" }).getAttribute("href")).toBe(
    `/tenants/${tenantId}/topics`,
  );
  expect(within(sheet).getByLabelText("Topic").hasAttribute("disabled")).toBe(true);
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

it("guides a new Subscription without exposing raw JSON editors", async () => {
  stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, key: "orders", name: "Orders", status: "active" }]) };
    if (url.pathname.endsWith("/destinations"))
      return { status: 200, body: page([{ id: destinationId, name: "Primary CRM", status: "active" }]) };
    return { status: 200, body: page([]) };
  });

  renderScreen(<SubscriptionsScreen tenantId={tenantId} />, `/tenants/${tenantId}/subscriptions?topic_id=${topicId}`);
  fireEvent.click(await screen.findByRole("button", { name: "New Subscription" }));
  const form = await screen.findByRole("form", { name: "Create a Subscription" });

  for (const heading of ["Routing", "HTTP request", "Mapping", "Response success"])
    expect(within(form).getByRole("heading", { name: heading })).toBeTruthy();
  expect(within(form).queryByLabelText(/JSON/)).toBeNull();
  expect(within(form).getByRole("button", { name: "Add mapping in Playground" })).toBeTruthy();
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
  const withSuccess = {
    ...subscription,
    http_success: { evaluator: "json_boolean", field: "accepted", expected: false },
  };
  const calls = stubHttp(({ method, url }) => {
    if (url.pathname.endsWith(`/subscriptions/${subscriptionId}`)) return { status: 200, body: withSuccess };
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
  fireEvent.change(within(form).getByLabelText("Boolean field"), { target: { value: "ok" } });
  expect(within(form).getByRole("combobox", { name: "Expected value" }).textContent).toContain("False");
  fireEvent.change(within(form).getByLabelText("Diagnostic field (optional)"), {
    target: { value: "message" },
  });
  fireEvent.change(within(form).getByLabelText("Maximum response bytes (optional)"), {
    target: { value: "4096" },
  });
  fireEvent.click(within(form).getByRole("button", { name: "Add header" }));
  fireEvent.change(within(form).getByLabelText("Header 1 name"), { target: { value: "X-Trace" } });
  fireEvent.change(within(form).getByLabelText("Header 1 value"), { target: { value: "" } });
  fireEvent.submit(form);
  await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));
  expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({
    http_success: {
      evaluator: "json_boolean",
      field: "ok",
      expected: false,
      diagnostic_field: "message",
      max_body_bytes: 4096,
    },
    http_delivery: {
      version: 1,
      method: "POST",
      path: null,
      headers: { "X-Trace": "" },
      body: "json",
    },
  });
});

describe("Editing a Subscription", () => {
  const detailPath = `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`;

  const orders = { id: topicId, key: "orders", name: "Orders", status: "active" };

  function stubEdit(detail: object, put: { status: number; body?: unknown } = { status: 200, body: detail }) {
    return stubHttp(({ method, url }) => {
      if (method === "PUT") return put;
      if (url.pathname.endsWith(`/subscriptions/${subscriptionId}`)) return { status: 200, body: detail };
      if (url.pathname.endsWith(`/topics/${topicId}`))
        return { status: 200, body: { ...orders, event_types: ["order.placed", "order.shipped"] } };
      if (url.pathname.endsWith("/topics")) return { status: 200, body: page([orders]) };
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
      event_types: subscription.event_types,
      destination_id: destinationId,
      mapping: subscription.mapping_config,
      http_delivery: subscription.http_delivery,
      http_success: httpSuccess,
      order_index: subscription.order_index,
      description: null,
    });
  });

  it("puts a refused Event type selection on the Event types", async () => {
    stubEdit(subscription, {
      status: 422,
      body: { errors: { event_types: ["Event type 'order.placed' is not declared by any Source on this Topic."] } },
    });
    const form = await submitUnchanged();

    expect(await within(form).findByText(/is not declared by any Source on this Topic/)).toBeTruthy();
  });

  /// Only a type some Source on the Topic declares can arrive, so the form offers those and no box to
  /// type another into, and it sends every type chosen.
  it("offers the Topic's declared Event types and sends each one chosen", async () => {
    const calls = stubEdit(subscription);
    renderScreen(
      <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
      detailPath,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    const form = await screen.findByRole("form", { name: `Edit ${subscription.name}` });
    const selection = within(form).getByRole("group", { name: "Event types" });

    const shipped = (await within(selection).findByRole("checkbox", { name: "order.shipped" })) as HTMLInputElement;
    expect((within(selection).getByRole("checkbox", { name: "order.placed" }) as HTMLInputElement).checked).toBe(true);
    expect(within(selection).queryByRole("textbox")).toBeNull();
    fireEvent.click(shipped);
    fireEvent.submit(form);

    await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));
    expect((calls.find((call) => call.method === "PUT")!.body as { event_types: unknown }).event_types).toEqual([
      "order.placed",
      "order.shipped",
    ]);
  });

  it("preserves an extended mapping through its raw fallback", async () => {
    const mapping = {
      engine: "jsonata",
      version: "1",
      expression: '{"id": id}',
      extension: true,
    };
    const calls = stubEdit({ ...subscription, mapping_config: mapping });
    renderScreen(
      <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
      detailPath,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    const form = await screen.findByRole("form", { name: `Edit ${subscription.name}` });

    expect(within(form).getByLabelText("Raw mapping (JSON)")).toBeTruthy();
    expect(within(form).queryByRole("button", { name: /Playground/ })).toBeNull();
    fireEvent.submit(form);

    await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));
    expect((calls.find((call) => call.method === "PUT")!.body as { mapping: unknown }).mapping).toEqual(mapping);
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
