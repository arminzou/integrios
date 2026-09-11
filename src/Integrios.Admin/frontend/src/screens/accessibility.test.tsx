import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { expectNoAccessibilityViolations } from "../test/axe";
import { page, stubHttp } from "../test/http";
import { renderScreen as renderInRouter } from "../test/router";
import { ConnectorsScreen } from "./Connectors";
import { DestinationsScreen } from "./Destinations";
import { EventsScreen } from "./Events";
import { SourcesScreen } from "./Sources";
import { SubscriptionsScreen } from "./Subscriptions";
import { TenantApiKeysScreen } from "./TenantApiKeys";
import { TenantScreen, TenantsScreen } from "./Tenants";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const eventId = "55555555-5555-5555-5555-555555555555";

const tenant = {
  id: tenantId,
  slug: "acme",
  name: "Acme",
  status: "active",
  environment: "production",
  description: null,
  created_at: "2026-09-01T00:00:00Z",
  updated_at: "2026-09-01T00:00:00Z",
};

const eventDetail = {
  event_id: eventId,
  status: "routed",
  accepted_at: "2026-09-01T10:00:00Z",
  processed_at: null,
  failed_at: null,
  trace_id: "0af7651916cd43dd8448eb211c80319c",
  event_deliveries: [
    {
      event_delivery_id: "66666666-6666-6666-6666-666666666666",
      subscription_id: "77777777-7777-7777-7777-777777777777",
      destination_id: "88888888-8888-8888-8888-888888888888",
      status: "dead_lettered",
      lifetime_attempt_count: 5,
      retry_cycle_attempt_count: 2,
      deliver_after: null,
      failed_at: "2026-09-01T10:05:00Z",
    },
  ],
  delivery_attempts: [
    {
      attempt_id: "99999999-9999-9999-9999-999999999999",
      event_delivery_id: "66666666-6666-6666-6666-666666666666",
      subscription_id: "77777777-7777-7777-7777-777777777777",
      destination_id: "88888888-8888-8888-8888-888888888888",
      attempt_number: 1,
      status: "failed",
      failure_phase: "response",
      response_status_code: 500,
      error_message: "The destination returned 500.",
      started_at: "2026-09-01T10:01:00Z",
      completed_at: "2026-09-01T10:01:02Z",
    },
  ],
};

/// Screens are rendered inside a landmark because that is how the shell renders them; asserting on a
/// bare fragment would report a missing landmark the real page has.
function renderScreen(element: React.ReactElement, path = "/") {
  return renderInRouter(<main>{element}</main>, path).container;
}

/// Sheets, confirmations, and the Event Builder render through a portal onto `document.body`, and
/// Radix hides everything behind them, so scanning a screen's container once one is open re-checks
/// the background. The topmost open dialog is scanned instead.
async function openDialog() {
  return (await screen.findAllByRole("dialog")).at(-1)!;
}

describe("Accessibility of the Operator workflows", () => {
  it("passes the automated rules on the authoring screens", async () => {
    stubHttp(({ url }) => ({
      status: 200,
      body:
        url.pathname.endsWith("/tenants") || url.pathname.includes("/topics") || url.pathname.includes("/sources")
          ? page([])
          : page([]),
    }));

    // Each element is rendered on its own by `renderScreen` in the loop body, never as a sibling in
    // a rendered list, so React has nothing to reconcile and a key would say otherwise.
    // biome-ignore-start lint/correctness/useJsxKeyInIterable: fixtures rendered one at a time, not as a list
    for (const [name, element] of [
      ["Tenants", <TenantsScreen />],
      ["Connectors", <ConnectorsScreen />],
      ["Destinations", <DestinationsScreen tenantId={tenantId} />],
      ["Sources", <SourcesScreen tenantId={tenantId} />],
      ["Tenant API keys", <TenantApiKeysScreen tenantId={tenantId} />],
    ] as const) {
      const container = renderScreen(element);
      await screen.findByRole("heading", { level: 1 });
      await expectNoAccessibilityViolations(container);
      expect(name).toBeTruthy();
      cleanup();
    }
    // biome-ignore-end lint/correctness/useJsxKeyInIterable: end of the fixture array
  });

  it("passes the automated rules on the Destinations authoring pattern with its create panel open", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<DestinationsScreen tenantId={tenantId} />);
    await screen.findByRole("heading", { level: 1, name: "Destinations" });
    // The create form is only exercised for accessibility once its disclosure is open — closed, it
    // carries no violations to find.
    fireEvent.click(screen.getByText("New Destination"));
    await expectNoAccessibilityViolations(await openDialog());
  });

  it("passes the automated rules on the Source authoring form and its Event Builder entry point", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<SourcesScreen tenantId={tenantId} />);
    await screen.findByRole("heading", { level: 1, name: "Sources" });
    fireEvent.click(screen.getByText("New Source"));
    await screen.findByRole("button", { name: "Create Source" });
    await expectNoAccessibilityViolations(await openDialog());
  });

  it("passes the automated rules on the Connector authoring sheet", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectorsScreen />);
    await screen.findByRole("heading", { level: 1, name: "Connectors" });
    // The guided draft is a form of native checkboxes, radios and fieldsets, so it is checked in the
    // state that has them rather than only in the screen's resting one.
    fireEvent.click(screen.getByRole("button", { name: "New Connector" }));
    await screen.findByRole("button", { name: "Create Connector" });
    await expectNoAccessibilityViolations(await openDialog());
  });

  it("passes the automated rules on a populated table and its confirmation", async () => {
    stubHttp(() => ({ status: 200, body: tenant }));

    const container = renderScreen(<TenantScreen tenantId={tenantId} />);
    await screen.findByRole("heading", { level: 1, name: "Overview" });
    await expectNoAccessibilityViolations(container);

    // Editing and the confirmation are states the screen only reaches on request, so they are
    // checked in those states rather than only in its resting one.
    fireEvent.click(screen.getByRole("button", { name: "Deactivate" }));
    await expectNoAccessibilityViolations(await openDialog());
  });

  it("passes the automated rules on the investigation screens", async () => {
    stubHttp(({ url }) => ({
      status: 200,
      body: url.pathname.endsWith("/deliveries") ? eventDetail : page([]),
    }));

    const events = renderScreen(<EventsScreen tenantId={tenantId} />);
    await screen.findByRole("heading", { level: 1, name: "Events" });
    await expectNoAccessibilityViolations(events);
    cleanup();

    const event = renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    await screen.findByRole("heading", { level: 1, name: "Events" });
    await expectNoAccessibilityViolations(event);
  });

  it("passes the automated rules on the Tenant-wide Subscriptions list", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    const container = renderScreen(<SubscriptionsScreen tenantId={tenantId} />);
    await screen.findByRole("heading", { level: 1, name: "Subscriptions" });
    await expectNoAccessibilityViolations(container);
  });
});

describe("Accessibility of detail and edit states", () => {
  const connector = {
    id: "22222222-2222-2222-2222-222222222222",
    key: "http",
    contract_version: 1,
    manifest_schema_version: 1,
    name: "HTTP",
    direction: "both",
    status: "active",
    description: null,
    manifest: { key: "http" },
    created_at: "2026-09-01T00:00:00Z",
    updated_at: "2026-09-01T00:00:00Z",
  };
  const destinationId = "44444444-4444-4444-4444-444444444444";
  const destination = {
    id: destinationId,
    tenant_id: tenantId,
    connector_id: connector.id,
    name: "northwind-erp",
    status: "active",
    environment: null,
    description: null,
    configuration: { base_uri: "http://erp.internal/hooks" },
    authentication: null,
    created_at: "2026-09-01T00:00:00Z",
    updated_at: "2026-09-01T00:00:00Z",
  };
  const topicId = "33333333-3333-3333-3333-333333333333";
  const subscriptionId = "66666666-6666-6666-6666-666666666666";
  const subscription = {
    id: subscriptionId,
    tenant_id: tenantId,
    topic_id: topicId,
    name: "Send priority orders",
    match_rules: { event_type: "order.placed" },
    destination_id: destinationId,
    mapping_config: null,
    http_delivery: { version: 1, method: "POST", path: null, headers: {}, body: "json" },
    http_success: null,
    status: "active",
    order_index: 0,
    description: null,
    created_at: "2026-09-01T00:00:00Z",
    updated_at: "2026-09-01T00:00:00Z",
  };

  it("passes the automated rules on a selected Destination and its edit sheet", async () => {
    stubHttp(({ url }) => {
      if (url.pathname.endsWith(`/destinations/${destinationId}`)) return { status: 200, body: destination };
      if (url.pathname.endsWith("/destinations")) return { status: 200, body: page([destination]) };
      if (url.pathname.endsWith("/connectors")) return { status: 200, body: page([connector]) };
      return { status: 200, body: page([]) };
    });

    const container = renderScreen(<DestinationsScreen tenantId={tenantId} selectedDestinationId={destinationId} />);
    await screen.findByRole("link", { name: "http v1" });
    await expectNoAccessibilityViolations(container);

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    await screen.findByRole("form", { name: `Edit ${destination.name}` });
    await expectNoAccessibilityViolations(await openDialog());
  });

  it("passes the automated rules on a selected Subscription and its edit sheet", async () => {
    stubHttp(({ url }) => {
      if (url.pathname.endsWith(`/subscriptions/${subscriptionId}`)) return { status: 200, body: subscription };
      if (url.pathname.endsWith("/destinations"))
        return { status: 200, body: page([{ id: destinationId, name: "northwind-erp", status: "active" }]) };
      return { status: 200, body: page([]) };
    });

    const container = renderScreen(
      <SubscriptionsScreen tenantId={tenantId} selectedTopicId={topicId} selectedSubscriptionId={subscriptionId} />,
      `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`,
    );
    await screen.findByRole("heading", { name: subscription.name });
    await expectNoAccessibilityViolations(container);

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    await screen.findByRole("form", { name: `Edit ${subscription.name}` });
    await expectNoAccessibilityViolations(await openDialog());
  });

  it("passes the automated rules on a selected Connector and its new-version sheet", async () => {
    stubHttp(({ url }) =>
      url.pathname === `/admin/connectors/${connector.id}`
        ? { status: 200, body: connector }
        : { status: 200, body: page([connector]) },
    );

    const container = renderScreen(
      <ConnectorsScreen selectedConnectorId={connector.id} />,
      `/connectors/${connector.id}`,
    );
    await screen.findByRole("heading", { name: new RegExp(connector.name) });
    await expectNoAccessibilityViolations(container);

    fireEvent.click(screen.getByRole("button", { name: "Create new version" }));
    await screen.findByRole("button", { name: "Create version" });
    await expectNoAccessibilityViolations(await openDialog());
  });

  it("passes the automated rules on the open Event Builder", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<SourcesScreen tenantId={tenantId} />);
    await screen.findByRole("heading", { level: 1, name: "Sources" });
    fireEvent.click(screen.getByText("New Source"));
    fireEvent.click(await screen.findByRole("button", { name: "Open Integrios Event Builder" }));
    await screen.findByRole("button", { name: "Close the Integrios Event Builder" });
    await expectNoAccessibilityViolations(await openDialog());

    // Each header row's controls carry a placeholder, which axe accepts as a name on its own. The
    // placeholder disappears once a value is typed, so the label is asserted directly.
    expect(screen.getByLabelText("Header 1 name")).toBeTruthy();
    expect(screen.getByLabelText("Header 1 representative value")).toBeTruthy();
  });
});
