import { afterEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { EventsScreen } from "./Events";
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

// Deliberately not minute-aligned: a rolling 60-minute window's end is normally "now", which lands
// mid-second. The round trip through the datetime-local inputs must preserve this to the second
// rather than rounding down and silently excluding Events the summary's own count included.
const activitySummary = {
  events_accepted: 5,
  awaiting_routing: 1,
  unrouted: 1,
  dead_lettered_deliveries: 2,
  window_start: "2026-09-01T09:00:17Z",
  window_end: "2026-09-01T10:00:47Z",
};

const eventsCall = (calls: Call[]) => calls.filter((call) => call.url.pathname.endsWith("/events"));

/// Distinguishes the ledger list, the activity summary, and an Event's own detail read, all of which
/// share the `.../events` path prefix, and falls the Source/Topic option reads back to an empty page.
function respondFor(eventsBody: unknown, detailBody: unknown = page([])) {
  return ({ url, method }: Call) => {
    if (method === "POST") return { status: 202 };
    if (url.pathname.endsWith("/activity-summary")) return { status: 200, body: activitySummary };
    if (url.pathname.endsWith("/deliveries")) return { status: 200, body: detailBody };
    if (url.pathname.endsWith("/events")) return { status: 200, body: eventsBody };
    return { status: 200, body: page([]) };
  };
}

function openFilters() {
  fireEvent.click(screen.getByText("Find an Event"));
}

describe("Event history", () => {
  it("reports Event status separately from Delivery state instead of rolling one into the other", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters])));

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
    const calls = stubHttp(respondFor(page([])));

    render(<EventsScreen tenantId={tenantId} />);
    await screen.findByText("No Events in this Tenant match these filters.");

    openFilters();
    fireEvent.change(screen.getByLabelText("Delivery status"), { target: { value: "dead_lettered" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply filters" }));

    await waitFor(() => expect(eventsCall(calls).length).toBeGreaterThan(1));
    const applied = eventsCall(calls).at(-1)!;
    expect(applied.url.searchParams.get("delivery_status")).toBe("dead_lettered");
    expect(applied.url.searchParams.has("status")).toBe(false);
    // A changed filter restarts from the first cursor rather than reusing one issued for the old scope.
    expect(applied.url.searchParams.has("after")).toBe(false);
  });

  it("reports unavailable filter options instead of presenting an empty picker", async () => {
    stubHttp((call) =>
      call.url.pathname.endsWith("/sources")
        ? { status: 500, body: { title: "Sources are unavailable." } }
        : respondFor(page([]))(call),
    );

    render(<EventsScreen tenantId={tenantId} />);
    openFilters();

    expect((await screen.findByRole("alert")).textContent).toContain("Sources are unavailable.");
    expect((screen.getByLabelText("Source") as HTMLSelectElement).disabled).toBe(true);
  });
});

describe("Event activity summary", () => {
  it("names the window and reports the four counts as pressable, unselected buttons", async () => {
    stubHttp(respondFor(page([])));

    render(<EventsScreen tenantId={tenantId} />);

    expect(await screen.findByText(/2026-09-01T09:00:17Z to 2026-09-01T10:00:47Z/)).toBeTruthy();
    for (const [label, value] of [
      ["Events accepted", "5"],
      ["Awaiting routing", "1"],
      ["Unrouted", "1"],
      ["Dead-lettered Deliveries", "2"],
    ]) {
      const button = screen.getByRole("button", { name: new RegExp(`${value}\\s*${label}`) });
      expect(button.getAttribute("aria-pressed")).toBe("false");
    }
  });

  it("applies the documented filters and time range, marks itself pressed, and restarts paging", async () => {
    const calls = stubHttp(respondFor(page([])));

    render(<EventsScreen tenantId={tenantId} />);
    const unrouted = await screen.findByRole("button", { name: /Unrouted/ });
    fireEvent.click(unrouted);

    expect(unrouted.getAttribute("aria-pressed")).toBe("true");
    await waitFor(() => expect(eventsCall(calls).length).toBeGreaterThan(1));
    const applied = eventsCall(calls).at(-1)!;
    expect(applied.url.searchParams.get("status")).toBe("unrouted");
    expect(applied.url.searchParams.has("delivery_status")).toBe(false);
    // The visible 60-minute window is applied to the ledger's own accepted-range filter, preserved
    // to the second rather than rounded down to the minute.
    expect(applied.url.searchParams.get("accepted_from")).toBe("2026-09-01T09:00:17.000Z");
    expect(applied.url.searchParams.get("accepted_to")).toBe("2026-09-01T10:00:47.000Z");
    expect(applied.url.searchParams.has("after")).toBe(false);

    // Editing a filter by hand deselects the summary item, so its pressed state never lies.
    openFilters();
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    expect(unrouted.getAttribute("aria-pressed")).toBe("false");
  });

  it("keeps the list's row count independent of the summary's own bounded counts", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters])));

    render(<EventsScreen tenantId={tenantId} />);
    await screen.findByRole("link", { name: "2026-09-01T10:00:00Z" });

    // The activity summary's "Events accepted" is the 60-minute window count (5), which the list's
    // own single visible row must never be mistaken for.
    const acceptedButton = screen.getByRole("button", { name: /Events accepted/ });
    expect(within(acceptedButton).getByText("5")).toBeTruthy();
    expect(screen.getAllByRole("row")).toHaveLength(2); // header row plus the one Event row
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

describe("Event inspector", () => {
  it("offers replay only for a dead-lettered Delivery, names it, and calls nothing until confirmed", async () => {
    const calls = stubHttp(respondFor(page([]), detail("dead_lettered")));

    render(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
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
    stubHttp(respondFor(page([]), detail("succeeded")));

    render(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    await screen.findByText("succeeded");
    expect(screen.queryByRole("button", { name: "Replay" })).toBeNull();
  });

  it("renders an in-progress Delivery attempt neutrally, not as a failure", async () => {
    // A worker leases an attempt as "in_progress" before it finishes, and a dead lease can leave one
    // stuck there indefinitely. Neither is a failure, and the timeline must not guess otherwise.
    const inProgress = {
      attempt_id: "88888888-0000-0000-0000-000000000001",
      event_delivery_id: deliveryId,
      subscription_id: subscriptionId,
      destination_connection_id: "88888888-8888-8888-8888-888888888888",
      attempt_number: 1,
      status: "in_progress",
      failure_phase: null,
      response_status_code: null,
      error_message: null,
      started_at: "2026-09-01T10:00:00Z",
      completed_at: null,
    };
    stubHttp(respondFor(page([]), { ...detail("succeeded"), delivery_attempts: [inProgress] }));

    render(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    const item = (await screen.findByText(/in_progress/)).closest("li")!;

    expect(item.className).not.toContain("failed");
    // No fabricated failure detail line for an attempt that has not finished yet.
    expect(within(item).queryByText(/HTTP/)).toBeNull();
  });

  it("hands over the trace identity as an opaque value without linking to any backend", async () => {
    stubHttp(respondFor(page([]), detail("succeeded")));

    render(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    const traceField = (await screen.findByLabelText("Trace id")) as HTMLInputElement;

    expect(traceField.value).toBe("0af7651916cd43dd8448eb211c80319c");
    expect(traceField.readOnly).toBe(true);
    // The dashboard does not know where traces live: no link may carry the trace id anywhere.
    for (const link of screen.queryAllByRole("link"))
      expect(link.getAttribute("href")).not.toContain("0af7651916cd43dd8448eb211c80319c");
  });

  it("marks the selected Event's row current and still renders the detail when the row is not loaded", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters]), detail("succeeded")));

    render(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);

    const row = (await screen.findByRole("link", { name: "2026-09-01T10:00:00Z" })).closest("tr")!;
    expect(row.querySelector("a[aria-current='page']")).toBeTruthy();
    // The inspector reads by the route's own Event id, independent of whether that row is loaded.
    await screen.findByRole("heading", { level: 2, name: `Event ${eventId}` });
  });
});
