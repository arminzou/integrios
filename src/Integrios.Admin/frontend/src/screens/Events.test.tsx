import { afterEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { EventScreen, EventsScreen } from "./Events";
import { page, stubHttp, type Call } from "../test/http";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const eventId = "55555555-5555-5555-5555-555555555555";
const deliveryId = "66666666-6666-6666-6666-666666666666";
const subscriptionId = "77777777-7777-7777-7777-777777777777";

/// An Event that was routed successfully and whose Deliveries then failed. Event status and Delivery
/// state disagree here on purpose: that disagreement is the thing the UI must not paper over.
const routedEventWithDeadLetters = {
  event_id: eventId,
  source_id: null,
  topic_id: null,
  source_event_id: "order-42",
  event_type: "order.created",
  status: "routed",
  accepted_at: "2026-09-01T10:00:00Z",
  trace_id: "0af7651916cd43dd8448eb211c80319c",
  deliveries: { pending: 0, in_flight: 0, succeeded: 1, dead_lettered: 2 },
};

const eventsCall = (calls: Call[]) => calls.filter((call) => call.url.pathname.endsWith("/events"));

describe("Event history", () => {
  it("reports Event status separately from Delivery state instead of rolling one into the other", async () => {
    stubHttp(() => ({ status: 200, body: page([routedEventWithDeadLetters]) }));

    render(<EventsScreen tenantId={tenantId} />);

    const row = (await screen.findByRole("link", { name: "2026-09-01T10:00:00Z" })).closest("tr")!;
    const cells = within(row).getAllByRole("cell");
    // The Event status cell reports the Event's own status. A dead-lettered Delivery does not
    // change it, and the Delivery cell names the state it is counting.
    expect(cells[2].textContent).toBe("routed");
    expect(cells[3].textContent).toContain("2 dead-lettered");
    expect(cells[3].textContent).toContain("1 succeeded");
    expect(row.textContent).not.toContain("dead_lettered");
  });

  it("sends a Delivery status filter as its own parameter and leaves Event status unset", async () => {
    const calls = stubHttp(() => ({ status: 200, body: page([]) }));

    render(<EventsScreen tenantId={tenantId} />);
    await screen.findByText("No Events in this Tenant match these filters.");

    fireEvent.change(screen.getByLabelText("Delivery status"), { target: { value: "dead_lettered" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply filters" }));

    await waitFor(() => expect(eventsCall(calls).length).toBeGreaterThan(1));
    const applied = eventsCall(calls).at(-1)!;
    expect(applied.url.searchParams.get("delivery_status")).toBe("dead_lettered");
    expect(applied.url.searchParams.has("status")).toBe(false);
    // A changed filter restarts from the first cursor rather than reusing one issued for the old scope.
    expect(applied.url.searchParams.has("after")).toBe(false);
  });
});

const detail = (deliveryStatus: string) => ({
  event_id: eventId,
  status: "routed",
  accepted_at: "2026-09-01T10:00:00Z",
  processed_at: "2026-09-01T10:00:01Z",
  failed_at: null,
  trace_id: "0af7651916cd43dd8448eb211c80319c",
  event_deliveries: [
    {
      event_delivery_id: deliveryId,
      subscription_id: subscriptionId,
      destination_connection_id: "88888888-8888-8888-8888-888888888888",
      status: deliveryStatus,
      lifetime_attempt_count: 5,
      retry_cycle_attempt_count: 2,
      deliver_after: null,
      failed_at: "2026-09-01T10:05:00Z",
    },
  ],
  delivery_attempts: [],
});

describe("Event investigation", () => {
  it("offers replay only for a dead-lettered Delivery, names it, and calls nothing until confirmed", async () => {
    const calls = stubHttp(({ method }) =>
      method === "POST" ? { status: 202 } : { status: 200, body: detail("dead_lettered") },
    );

    render(<EventScreen tenantId={tenantId} eventId={eventId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Replay" }));

    expect(screen.getByText(new RegExp(`Replay the dead-lettered delivery to Subscription ${subscriptionId}`))).toBeTruthy();
    expect(calls.some((call) => call.method === "POST")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Replay this delivery" }));

    await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
    expect(calls.find((call) => call.method === "POST")!.url.pathname).toBe(
      `/admin/tenants/${tenantId}/events/${eventId}/deliveries/${deliveryId}/replay`,
    );
  });

  it("does not offer replay for a Delivery the API cannot replay", async () => {
    stubHttp(() => ({ status: 200, body: detail("succeeded") }));

    render(<EventScreen tenantId={tenantId} eventId={eventId} />);
    await screen.findByText("succeeded");
    expect(screen.queryByRole("button", { name: "Replay" })).toBeNull();
  });

  it("hands over the trace identity as an opaque value without linking to any backend", async () => {
    stubHttp(() => ({ status: 200, body: detail("succeeded") }));

    render(<EventScreen tenantId={tenantId} eventId={eventId} />);
    const traceField = (await screen.findByLabelText("Trace id")) as HTMLInputElement;

    expect(traceField.value).toBe("0af7651916cd43dd8448eb211c80319c");
    expect(traceField.readOnly).toBe(true);
    // The dashboard does not know where traces live: no link may carry the trace id anywhere.
    for (const link of screen.queryAllByRole("link"))
      expect(link.getAttribute("href")).not.toContain("0af7651916cd43dd8448eb211c80319c");
  });
});
