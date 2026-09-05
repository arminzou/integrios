import { afterEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { expectNoAccessibilityViolations } from "../test/axe";
import { page, stubHttp } from "../test/http";
import { ConnectionsScreen } from "./Connections";
import { ConnectorsScreen } from "./Connectors";
import { EventsScreen } from "./Events";
import { SourcesScreen } from "./Sources";
import { TenantApiKeysScreen } from "./TenantApiKeys";
import { TenantScreen, TenantsScreen } from "./Tenants";
import { TopicScreen } from "./Topics";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
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

const topic = {
  id: topicId,
  tenant_id: tenantId,
  name: "orders",
  status: "active",
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
      destination_connection_id: "88888888-8888-8888-8888-888888888888",
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
      destination_connection_id: "88888888-8888-8888-8888-888888888888",
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
function renderScreen(element: React.ReactElement) {
  return render(<main>{element}</main>).container;
}

describe("Accessibility of the Operator workflows", () => {
  it("passes the automated rules on the authoring screens", async () => {
    stubHttp(({ url }) => ({
      status: 200,
      body: url.pathname.endsWith("/tenants") || url.pathname.includes("/topics") || url.pathname.includes("/sources")
        ? page([])
        : page([]),
    }));

    for (const [name, element] of [
      ["Tenants", <TenantsScreen />],
      ["Connectors", <ConnectorsScreen />],
      ["Connections", <ConnectionsScreen tenantId={tenantId} />],
      ["Sources", <SourcesScreen tenantId={tenantId} />],
      ["Tenant API keys", <TenantApiKeysScreen tenantId={tenantId} />],
    ] as const) {
      const container = renderScreen(element);
      await screen.findByRole("heading", { level: 1 });
      await expectNoAccessibilityViolations(container);
      expect(name).toBeTruthy();
      cleanup();
    }
  });

  it("passes the automated rules on the Connections authoring pattern with its create panel open", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    const container = renderScreen(<ConnectionsScreen tenantId={tenantId} />);
    await screen.findByRole("heading", { level: 1, name: "Connections" });
    // The create form is only exercised for accessibility once its disclosure is open — closed, it
    // carries no violations to find.
    fireEvent.click(screen.getByText("New Connection"));
    await expectNoAccessibilityViolations(container);
  });

  it("passes the automated rules on a populated table and its confirmation", async () => {
    stubHttp(() => ({ status: 200, body: tenant }));

    const container = renderScreen(<TenantScreen tenantId={tenantId} />);
    await screen.findByRole("heading", { level: 1, name: "Acme" });
    await expectNoAccessibilityViolations(container);

    // The confirmation is a state the screen only reaches on request, so it is checked in that state
    // rather than only in its resting one.
    fireEvent.click(screen.getByRole("button", { name: "Deactivate Tenant" }));
    await expectNoAccessibilityViolations(container);
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

  it("passes the automated rules on a Topic and its Subscriptions", async () => {
    stubHttp(({ url }) => ({
      status: 200,
      body: url.pathname.endsWith("/subscriptions") || url.pathname.endsWith("/connections")
        ? page([])
        : topic,
    }));

    const container = renderScreen(<TopicScreen tenantId={tenantId} topicId={topicId} />);
    await screen.findByRole("heading", { level: 1, name: "orders" });
    await expectNoAccessibilityViolations(container);
  });
});
