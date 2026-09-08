import { cleanup, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { SubscriptionsScreen } from "./Subscriptions";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const connectionId = "33333333-3333-3333-3333-333333333333";
const subscriptionId = "44444444-4444-4444-4444-444444444444";

it("lists Tenant Subscriptions with their Topic and destination names and sends every filter", async () => {
  const calls = stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, name: "Orders", status: "active" }]) };
    if (url.pathname.endsWith("/connections"))
      return { status: 200, body: page([{ id: connectionId, name: "Primary CRM", status: "active" }]) };
    return {
      status: 200,
      body: page([
        {
          id: subscriptionId,
          tenant_id: tenantId,
          topic_id: topicId,
          topic_name: "Orders",
          destination_connection_id: connectionId,
          destination_connection_name: "Primary CRM",
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
    `/tenants/${tenantId}/subscriptions?name=priority&topic_id=${topicId}&connection_id=${connectionId}&status=active`,
  );

  expect(await screen.findByRole("link", { name: "Send priority orders" })).toBeTruthy();
  expect(screen.getByRole("link", { name: "Orders" }).getAttribute("href")).toBe(
    `/tenants/${tenantId}/topics/${topicId}`,
  );
  expect(screen.getByRole("link", { name: "Primary CRM" }).getAttribute("href")).toBe(
    `/tenants/${tenantId}/connections/${connectionId}`,
  );
  expect(screen.getByRole("columnheader", { name: "Destination Connection" })).toBeTruthy();
  await waitFor(() => {
    const list = calls.find(({ url }) => url.pathname.endsWith("/subscriptions"));
    expect(Object.fromEntries(list!.url.searchParams)).toMatchObject({
      name: "priority",
      topic_id: topicId,
      connection_id: connectionId,
      status: "active",
    });
  });
});
