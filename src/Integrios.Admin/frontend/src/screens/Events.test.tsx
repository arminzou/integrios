import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { activityOf, type Call, page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { EventsScreen } from "./Events";

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

/// Current backlogs as the Admin API reports them: however old, with the oldest item's instant. The
/// unrouted backlog is a quiet zero on purpose, since a zero must stay on screen.
const backlog = {
  awaiting_routing: { count: 3, oldest_at: "2026-08-29T10:00:00Z" },
  unrouted: { count: 0, oldest_at: null },
  dead_lettered_deliveries: { count: 2, oldest_at: "2026-08-31T10:00:00Z" },
};

/// The last hour in twelve 5-minute buckets from 09:00, and a week in 6-hour buckets for the 7 d range.
const activityFor = (range: string | null) =>
  range === "7d"
    ? activityOf({}, { range: "7d", start: "2026-08-25T10:00:00Z", bucketMinutes: 360, bucketCount: 28 })
    : activityOf({ 9: { routed: 3, delivery_dead_lettered: 1 }, 10: { awaiting_routing: 2 } });

const eventsCall = (calls: Call[]) => calls.filter((call) => call.url.pathname.endsWith("/events"));

/// Distinguishes the ledger list, the backlog, and an Event's own detail read, all of which
/// share the `.../events` path prefix, and falls the Source/Topic option reads back to an empty page.
function respondFor(eventsBody: unknown, detailBody: unknown = page([])) {
  return ({ url, method }: Call) => {
    if (method === "POST") return { status: 202 };
    if (url.pathname.endsWith("/backlog")) return { status: 200, body: backlog };
    if (url.pathname.endsWith("/activity")) return { status: 200, body: activityFor(url.searchParams.get("range")) };
    if (url.pathname.endsWith("/deliveries")) return { status: 200, body: detailBody };
    if (url.pathname.endsWith("/events")) return { status: 200, body: eventsBody };
    return { status: 200, body: page([]) };
  };
}

/// A ledger row, located by the route its primary link points at rather than by the acceptance time
/// it renders. The visible instant is formatted for the reader's locale, so it is not a stable
/// handle; the route is, and it is what the row's selection contract is actually about.
async function ledgerRow(id: string): Promise<HTMLTableRowElement> {
  return await waitFor(() => {
    const row = document.querySelector(`a[href^="/tenants/${tenantId}/events/${id}"]`)?.closest("tr");
    if (!row) throw new Error(`No ledger row for Event ${id}.`);
    return row as HTMLTableRowElement;
  });
}

describe("Event history", () => {
  it("states the day once and leaves each row carrying only its time", async () => {
    const earlier = {
      ...routedEventWithDeadLetters,
      event_id: "11111111-2222-3333-4444-555555555555",
      accepted_at: "2026-08-31T22:15:00Z",
    };
    stubHttp(respondFor(page([routedEventWithDeadLetters, earlier])));

    renderScreen(<EventsScreen tenantId={tenantId} />);

    const row = await ledgerRow(eventId);
    // The instant the API sent is still on the attribute a machine reads, and still exact.
    const stamp = within(row).getByRole("rowheader").querySelector("time");
    expect(stamp?.getAttribute("datetime")).toBe("2026-09-01T10:00:00Z");
    // What is rendered no longer repeats the day the separator above it already states.
    expect(stamp?.textContent).not.toContain("Sep");

    // Two Events on different local days produce two separators, each naming its own group.
    expect(
      screen.getAllByRole("columnheader").filter((cell) => cell.getAttribute("scope") === "colgroup"),
    ).toHaveLength(2);
  });

  // The ledger has to say what it is showing without being asked. A filtered view that looks
  // identical to an unfiltered one is the failure this replaced a disclosure to prevent.
  it("states its scope on screen, and offers a clear only once something is applied", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters])));

    const unfiltered = renderScreen(<EventsScreen tenantId={tenantId} />, `/tenants/${tenantId}/events`);
    // Every control is reachable without opening anything.
    expect(await screen.findByLabelText("Event status")).toBeTruthy();
    expect(screen.getByLabelText("Delivery status")).toBeTruthy();
    const filters = screen.getByRole("region", { name: "Filters" });
    const sourceEventId = within(filters).getByRole("searchbox", { name: "Source Event id" });
    expect(filters.querySelector("input, button")).toBe(sourceEventId);
    expect(sourceEventId.getAttribute("placeholder")).toBe("Source Event id");
    expect(screen.queryByText(/filter.? applied/)).toBeNull();
    expect(screen.queryByRole("button", { name: "Clear filters" })).toBeNull();
    unfiltered.unmount();

    renderScreen(<EventsScreen tenantId={tenantId} />, `/tenants/${tenantId}/events?status=unrouted`);

    // How many filters are applied is stated on the list's own caption, beside what it is a list of.
    expect(await screen.findByText(/Events, newest first · 1 filter applied/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Clear filters" })).toBeTruthy();
  });

  it("reports Event status separately from Delivery state instead of rolling one into the other", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters])));

    renderScreen(<EventsScreen tenantId={tenantId} />);

    const row = await ledgerRow(eventId);
    // The exact instant the API sent survives formatting, on the attribute a machine reads.
    expect(within(row).getByRole("rowheader").querySelector("time")?.getAttribute("datetime")).toBe(
      "2026-09-01T10:00:00Z",
    );

    const cells = within(row).getAllByRole("cell");
    // The Event status cell reports the Event's own status. A dead-lettered Delivery does not
    // change it, and the Delivery cell names the state it is counting.
    expect(cells[2].textContent).toBe("Routed");
    expect(cells[3].textContent).toContain("2 dead-lettered");
    expect(cells[3].textContent).toContain("1 succeeded");
    expect(row.textContent).not.toContain("dead_lettered");
  });

  it("restores the ledger's scope from the URL, so a filtered view is a link", async () => {
    const calls = stubHttp(respondFor(page([])));

    renderScreen(
      <EventsScreen tenantId={tenantId} />,
      `/tenants/${tenantId}/events?status=unrouted&source_event_id=order-42&accepted_from=2026-09-01T09%3A00%3A17Z`,
    );

    await waitFor(() => expect(eventsCall(calls).length).toBeGreaterThan(0));
    const read = eventsCall(calls)[0].url.searchParams;
    expect(read.get("status")).toBe("unrouted");
    expect(read.get("source_event_id")).toBe("order-42");
    // The window survives the round trip through the local-time control the form holds it in.
    expect(read.get("accepted_from")).toBe("2026-09-01T09:00:17.000Z");

    // The form opens showing the scope in force, not the empty defaults.
    expect((screen.getByLabelText("Source Event id") as HTMLInputElement).value).toBe("order-42");
  });

  it("reports unavailable filter options instead of presenting an empty picker", async () => {
    // A ledger with a row in it, because the filter form is only offered over a ledger there is
    // something to narrow.
    stubHttp((call) =>
      call.url.pathname.endsWith("/sources")
        ? { status: 500, body: { title: "Sources are unavailable." } }
        : respondFor(page([routedEventWithDeadLetters]))(call),
    );

    renderScreen(<EventsScreen tenantId={tenantId} />);

    expect((await screen.findByRole("alert")).textContent).toContain("Sources are unavailable.");
    expect((screen.getByLabelText("Source") as HTMLSelectElement).disabled).toBe(true);
  });
});

describe("Opening an Event from its row", () => {
  it("opens from a cell that is not the link", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters])));

    const { router } = renderScreen(<EventsScreen tenantId={tenantId} />, `/tenants/${tenantId}/events`);
    fireEvent.click(await screen.findByText("order.created"));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/tenants/${tenantId}/events/${eventId}`));
  });

  it("leaves the copy control in a row to its own job", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters])));

    const { router } = renderScreen(<EventsScreen tenantId={tenantId} />, `/tenants/${tenantId}/events`);
    // The ledger carries an identifier an Operator copies far more often than they open the Event
    // it belongs to, so the button inside the row must not double as a way into the row.
    fireEvent.click(await screen.findByRole("button", { name: "Copy source event id" }));

    expect(router.state.location.pathname).toBe(`/tenants/${tenantId}/events`);
  });
});

describe("Right now", () => {
  it("reports every backlog with its oldest age, keeping a zero on screen", async () => {
    stubHttp(respondFor(page([])));

    renderScreen(<EventsScreen tenantId={tenantId} />);

    const strip = await screen.findByRole("region", { name: "Right now" });
    const awaiting = within(strip).getByRole("button", { name: /Awaiting routing/ });
    expect(awaiting.textContent).toContain("3");
    expect(awaiting.textContent).toMatch(/Oldest .*ago/);
    const unrouted = within(strip).getByRole("button", { name: /Unrouted/ });
    expect(unrouted.textContent).toContain("0");
    expect(unrouted.textContent).toContain("Nothing waiting");
    for (const button of within(strip).getAllByRole("button"))
      expect(button.getAttribute("aria-pressed")).toBe("false");
  });

  it("filters the ledger by the backlog's status alone, with no time range, and restarts paging", async () => {
    const calls = stubHttp(respondFor(page([routedEventWithDeadLetters])));

    const { router } = renderScreen(
      <EventsScreen tenantId={tenantId} />,
      `/tenants/${tenantId}/events?status=routed&accepted_from=2026-09-01T09%3A00%3A17Z&source_event_id=order-42`,
    );
    const dead = await screen.findByRole("button", { name: /Dead-lettered Deliveries/ });
    fireEvent.click(dead);

    await waitFor(() => expect(router.state.location.search).toBe("?delivery_status=dead_lettered"));
    expect(dead.getAttribute("aria-pressed")).toBe("true");
    await waitFor(() =>
      expect(eventsCall(calls).at(-1)?.url.searchParams.get("delivery_status")).toBe("dead_lettered"),
    );
    const applied = eventsCall(calls).at(-1)!.url.searchParams;
    for (const name of ["status", "accepted_from", "accepted_to", "source_event_id", "after"])
      expect(applied.has(name)).toBe(false);

    // Any other scope is no longer this backlog alone, so it stops reading as pressed.
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    await waitFor(() => expect(dead.getAttribute("aria-pressed")).toBe("false"));
  });
});

describe("Event activity", () => {
  it("names each interval by its time and every outcome count", async () => {
    stubHttp(respondFor(page([])));

    renderScreen(<EventsScreen tenantId={tenantId} />);

    const chart = await screen.findByRole("region", { name: "Activity" });
    await within(chart).findByText("6 Events accepted, by current outcome.");
    const intervals = within(chart).getByRole("group", { name: /Event activity intervals/ });
    const buttons = within(intervals).getAllByRole("button");
    expect(buttons).toHaveLength(12);
    expect(buttons[9].getAttribute("aria-label")).toMatch(
      /: 3 routed, 0 awaiting routing, 0 unrouted, 1 delivery dead-lettered$/,
    );
    // One tab stop for the whole chart, on the most recent interval.
    expect(buttons.filter((button) => button.tabIndex === 0)).toEqual([buttons[11]]);
  });

  it("scopes the ledger to a selected interval through a visible, removable filter", async () => {
    const calls = stubHttp(respondFor(page([routedEventWithDeadLetters])));

    const { router } = renderScreen(<EventsScreen tenantId={tenantId} />, `/tenants/${tenantId}/events?status=routed`);
    const intervals = await screen.findByRole("group", { name: /Event activity intervals/ });
    const tenth = within(intervals).getAllByRole("button")[9];
    fireEvent.click(tenth);

    await waitFor(() =>
      expect(eventsCall(calls).at(-1)?.url.searchParams.get("accepted_from")).toBe("2026-09-01T09:45:00.000Z"),
    );
    const read = eventsCall(calls).at(-1)!.url.searchParams;
    expect(read.get("accepted_to")).toBe("2026-09-01T09:50:00.000Z");
    // The rest of the scope is kept; only the accepted range comes from the chart.
    expect(read.get("status")).toBe("routed");
    expect(tenth.getAttribute("aria-pressed")).toBe("true");

    fireEvent.click(screen.getByRole("button", { name: "Remove time range" }));
    await waitFor(() => expect(router.state.location.search).toBe("?status=routed"));
    expect(tenth.getAttribute("aria-pressed")).toBe("false");
  });

  it("extends a selection with Shift and an arrow key, and moves focus with the arrow alone", async () => {
    const calls = stubHttp(respondFor(page([])));

    renderScreen(<EventsScreen tenantId={tenantId} />);
    const intervals = await screen.findByRole("group", { name: /Event activity intervals/ });
    const buttons = within(intervals).getAllByRole("button");
    buttons[11].focus();
    fireEvent.keyDown(buttons[11], { key: "ArrowLeft" });
    expect(document.activeElement).toBe(buttons[10]);
    expect(buttons[10].tabIndex).toBe(0);

    fireEvent.click(buttons[10]);
    fireEvent.keyDown(buttons[10], { key: "ArrowLeft", shiftKey: true });
    fireEvent.keyDown(buttons[9], { key: "ArrowLeft", shiftKey: true });

    await waitFor(() =>
      expect(eventsCall(calls).at(-1)?.url.searchParams.get("accepted_from")).toBe("2026-09-01T09:40:00.000Z"),
    );
    expect(eventsCall(calls).at(-1)!.url.searchParams.get("accepted_to")).toBe("2026-09-01T09:55:00.000Z");
    expect(document.activeElement).toBe(buttons[8]);
    // Contracting back towards the anchor narrows the range again.
    fireEvent.keyDown(buttons[8], { key: "ArrowRight", shiftKey: true });
    await waitFor(() =>
      expect(eventsCall(calls).at(-1)?.url.searchParams.get("accepted_from")).toBe("2026-09-01T09:45:00.000Z"),
    );
  });

  it("reads the week in 6-hour intervals when the 7 d range is chosen", async () => {
    const calls = stubHttp(respondFor(page([])));

    renderScreen(<EventsScreen tenantId={tenantId} />);
    fireEvent.click(await screen.findByRole("button", { name: "7 d" }));

    const intervals = await screen.findByRole("group", { name: /Event activity intervals/ });
    await waitFor(() => expect(within(intervals).getAllByRole("button")).toHaveLength(28));
    expect(
      calls.some((call) => call.url.pathname.endsWith("/activity") && call.url.searchParams.get("range") === "7d"),
    ).toBe(true);
    expect(screen.getByRole("button", { name: "7 d" }).getAttribute("aria-pressed")).toBe("true");
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
      destination_id: "88888888-8888-8888-8888-888888888888",
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
  /// A shown body is highlighted, so its text is spread across the spans that colour it and only
  /// the panel as a whole carries the document.
  const shownBody = (document: RegExp) =>
    screen.getByText((_, element) => element?.tagName === "PRE" && document.test(element.textContent ?? ""));

  it("shows what was accepted, what was sent, and what the destination returned", async () => {
    stubHttp(
      respondFor(page([routedEventWithDeadLetters]), {
        ...detail("dead_lettered"),
        payload: { orderId: "SO-4014", total: 1673 },
        delivery_attempts: [
          {
            attempt_id: "aaaaaaaa-0000-0000-0000-000000000001",
            event_delivery_id: deliveryId,
            subscription_id: subscriptionId,
            destination_id: "88888888-8888-8888-8888-888888888888",
            attempt_number: 3,
            status: "failed",
            failure_phase: "http",
            response_status_code: 503,
            error_message: null,
            started_at: "2026-09-01T10:00:02Z",
            completed_at: "2026-09-01T10:00:03Z",
            request_payload: { order: "SO-4014" },
            response_body: '{"error":"upstream unavailable"}',
            response_body_truncated: true,
          },
        ],
      }),
    );

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);

    expect(await screen.findByRole("heading", { name: "Accepted payload" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Event deliveries" })).toBeTruthy();
    expect(shownBody(/"orderId": "SO-4014"/)).toBeTruthy();
    // Read as code: the property name carries the key colour rather than the body's own.
    expect(screen.getByText('"orderId"').className).toContain("text-code-key");

    // What was sent is the mapped body, not the accepted one, and is labelled as such.
    expect(screen.getByRole("heading", { name: "Sent" })).toBeTruthy();
    expect(shownBody(/"order": "SO-4014"/)).toBeTruthy();

    // A stored fragment says so rather than reading as a whole response that ends strangely.
    expect(screen.getByRole("heading", { name: "Returned" })).toBeTruthy();
    expect(shownBody(/^\{"error":"upstream unavailable"\}$/)).toBeTruthy();
    expect(screen.getByText("Truncated")).toBeTruthy();
  });

  it("offers replay only for a dead-lettered Delivery, names it, and calls nothing until confirmed", async () => {
    const calls = stubHttp(respondFor(page([]), detail("dead_lettered")));

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Replay" }));

    expect(
      screen.getByText(new RegExp(`Replay the dead-lettered delivery to Subscription ${subscriptionId}`)),
    ).toBeTruthy();
    expect(calls.some((call) => call.method === "POST")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Replay this delivery" }));

    await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
    expect(calls.find((call) => call.method === "POST")!.url.pathname).toBe(
      `/admin/tenants/${tenantId}/events/${eventId}/deliveries/${deliveryId}/replay`,
    );
  });

  it("keeps the replay confirmation after the Delivery leaves the state that offered it", async () => {
    // A replayed Delivery stops being dead-lettered, which is the only state the replay control
    // renders under. The confirmation must not live inside that control: it would unmount at the
    // moment it finally had something to report, flashing rather than reporting.
    let deliveryStatus = "dead_lettered";
    stubHttp(({ url, method }) => {
      if (method === "POST") {
        deliveryStatus = "pending";
        return { status: 202 };
      }
      if (url.pathname.endsWith("/backlog")) return { status: 200, body: backlog };
      if (url.pathname.endsWith("/activity")) return { status: 200, body: activityFor("1h") };
      if (url.pathname.endsWith("/deliveries")) return { status: 200, body: detail(deliveryStatus) };
      return { status: 200, body: page([]) };
    });

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Replay" }));
    fireEvent.click(screen.getByRole("button", { name: "Replay this delivery" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "Replay" })).toBeNull());
    expect(screen.getByText("Queued for delivery again.")).toBeTruthy();
  });

  it("does not offer replay for a Delivery the API cannot replay", async () => {
    stubHttp(respondFor(page([]), detail("succeeded")));

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    await screen.findByLabelText("Trace id");
    expect(screen.queryByRole("button", { name: "Replay" })).toBeNull();
  });

  it("shows the most recent attempts and holds the rest of the retry history behind a control", async () => {
    // A destination that has been failing for a while accumulates attempts the inspector cannot show
    // at once in a 400-pixel panel. What an Operator is triaging is the recent end, so that is what
    // is shown unasked — and it must be the recent end, not the first five, or the panel would open
    // on history and hide the failure being investigated.
    const attempt = (number: number) => ({
      attempt_id: `aaaaaaaa-0000-0000-0000-00000000000${number}`,
      event_delivery_id: deliveryId,
      subscription_id: subscriptionId,
      destination_id: "88888888-8888-8888-8888-888888888888",
      attempt_number: number,
      status: "failed",
      failure_phase: "http",
      response_status_code: 503,
      error_message: null,
      started_at: `2026-09-01T10:0${number}:00Z`,
      completed_at: `2026-09-01T10:0${number}:01Z`,
    });
    const all = [1, 2, 3, 4, 5, 6, 7, 8].map(attempt);
    stubHttp(respondFor(page([]), { ...detail("dead_lettered"), delivery_attempts: all }));

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    const timeline = await screen.findByRole("list", {
      name: "Every delivery attempt for this Event",
    });

    expect(within(timeline).getAllByRole("listitem")).toHaveLength(5);
    expect(within(timeline).getByText(/attempt 8 to Subscription/)).toBeTruthy();
    expect(within(timeline).queryByText(/attempt 3 to Subscription/)).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Show all 8 attempts" }));

    expect(within(timeline).getAllByRole("listitem")).toHaveLength(8);
    expect(within(timeline).getByText(/attempt 1 to Subscription/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Show all/ })).toBeNull();
  });

  it("renders an in-progress Delivery attempt neutrally, not as a failure", async () => {
    // A worker leases an attempt as "in_progress" before it finishes, and a dead lease can leave one
    // stuck there indefinitely. Neither is a failure, and the timeline must not guess otherwise.
    const inProgress = {
      attempt_id: "88888888-0000-0000-0000-000000000001",
      event_delivery_id: deliveryId,
      subscription_id: subscriptionId,
      destination_id: "88888888-8888-8888-8888-888888888888",
      attempt_number: 1,
      status: "in_progress",
      failure_phase: null,
      response_status_code: null,
      error_message: null,
      started_at: "2026-09-01T10:00:00Z",
      completed_at: null,
    };
    stubHttp(respondFor(page([]), { ...detail("succeeded"), delivery_attempts: [inProgress] }));

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    const item = (await screen.findByText(/In progress/)).closest("li")!;

    // The failure tone itself, not the word "failed" — which this class list never contained, so
    // asserting on it passed whatever the marker was actually painted.
    expect(item.className).not.toContain("danger");
    // No fabricated failure detail line for an attempt that has not finished yet.
    expect(within(item).queryByText(/HTTP/)).toBeNull();
  });

  it("tells a succeeded, an unfinished and a failed attempt apart by word as well as by marker", async () => {
    // An Operator reads the timeline as a column of outcomes, so the three have to differ where the
    // eye lands — and differ by their own word, never by colour alone.
    const attempt = (number: number, status: string) => ({
      attempt_id: `cccccccc-0000-0000-0000-00000000000${number}`,
      event_delivery_id: deliveryId,
      subscription_id: subscriptionId,
      destination_id: "88888888-8888-8888-8888-888888888888",
      attempt_number: number,
      status,
      failure_phase: status === "failed" ? "http" : null,
      response_status_code: status === "failed" ? 503 : null,
      error_message: null,
      started_at: `2026-09-01T10:0${number}:00Z`,
      completed_at: status === "in_progress" ? null : `2026-09-01T10:0${number}:01Z`,
    });
    stubHttp(
      respondFor(page([]), {
        ...detail("dead_lettered"),
        delivery_attempts: [attempt(1, "succeeded"), attempt(2, "in_progress"), attempt(3, "failed")],
      }),
    );

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    const timeline = await screen.findByRole("list", {
      name: "Every delivery attempt for this Event",
    });
    const entries = within(timeline).getAllByRole("listitem");

    expect(within(entries[0]).getByText("Succeeded")).toBeTruthy();
    expect(within(entries[1]).getByText("In progress")).toBeTruthy();
    expect(within(entries[2]).getByText("Failed")).toBeTruthy();
    // Three outcomes, three markers: not the binary that painted a leased attempt like a settled one.
    expect(new Set(entries.map((entry) => entry.className)).size).toBe(3);
  });

  it("hands over the trace identity as an opaque value without linking to any backend", async () => {
    stubHttp(respondFor(page([]), detail("succeeded")));

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    const traceField = (await screen.findByLabelText("Trace id")) as HTMLInputElement;

    expect(traceField.value).toBe("0af7651916cd43dd8448eb211c80319c");
    expect(traceField.readOnly).toBe(true);
    // The dashboard does not know where traces live: no link may carry the trace id anywhere.
    for (const link of screen.queryAllByRole("link"))
      expect(link.getAttribute("href")).not.toContain("0af7651916cd43dd8448eb211c80319c");
  });

  it("marks the selected Event's row current and still renders the detail when the row is not loaded", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters]), detail("succeeded")));

    // The row's current state is the route, not a prop: the ledger link marks itself when the URL
    // is that Event's own, which is what makes a copied link and a clicked row agree.
    renderScreen(
      <EventsScreen tenantId={tenantId} selectedEventId={eventId} />,
      `/tenants/${tenantId}/events/${eventId}`,
    );

    const row = await ledgerRow(eventId);
    expect(row.querySelector("a[aria-current='page']")).toBeTruthy();
    // The inspector reads by the route's own Event id, independent of whether that row is loaded.
    await screen.findByRole("heading", { level: 2, name: `Event ${eventId}` });
  });
});
