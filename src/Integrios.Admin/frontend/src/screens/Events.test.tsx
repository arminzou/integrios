import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
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
    expect(sourceEventId.getAttribute("placeholder")).toBe("Exact id…");
    expect(screen.queryByRole("button", { name: "Apply filters" })).toBeNull();
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
    // The window is read as the link's own instant, not re-derived from the local-time control.
    expect(read.get("accepted_from")).toBe("2026-09-01T09:00:17Z");

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

describe("Event filter hints", () => {
  it("describes the capped Source and Topic options to assistive technology", async () => {
    stubHttp((call) =>
      call.url.pathname.endsWith("/sources") || call.url.pathname.endsWith("/topics")
        ? { status: 200, body: page([], "more") }
        : respondFor(page([routedEventWithDeadLetters]))(call),
    );

    renderScreen(<EventsScreen tenantId={tenantId} />);

    for (const [name, id, text] of [
      ["Source", "event-source-hint", "Showing the first 100 Sources."],
      ["Topic", "event-topic-hint", "Showing the first 100 Topics."],
    ]) {
      const control = await screen.findByRole("combobox", { name });
      await waitFor(() => expect(control.getAttribute("aria-describedby")).toBe(id));
      expect(document.getElementById(id)!.textContent).toBe(text);
    }
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

describe("Ledger Event type and freshness", () => {
  it("filters the ledger by the Event type typed, and restores it from the URL", async () => {
    const calls = stubHttp(respondFor(page([routedEventWithDeadLetters])));

    const { router } = renderScreen(<EventsScreen tenantId={tenantId} />);
    const type = await screen.findByLabelText("Event type");
    const reads = eventsCall(calls).length;
    fireEvent.change(type, { target: { value: "Order.Created" } });

    // Typed, not committed: no read, no history entry, and the pill is not tinted as scope.
    expect(eventsCall(calls).length).toBe(reads);
    expect(router.state.location.search).toBe("");
    expect(type.closest("form")!.getAttribute("data-applied")).toBe("false");

    fireEvent.submit(type.closest("form")!);

    await waitFor(() => expect(eventsCall(calls).at(-1)?.url.searchParams.get("event_type")).toBe("Order.Created"));
    expect(router.state.location.search).toBe("?event_type=Order.Created");
    expect(type.closest("form")!.getAttribute("data-applied")).toBe("true");
  });

  it("counts new Events without moving rows, and Show reloads the first page under the next watermark", async () => {
    let listReads = 0;
    const second = {
      ...routedEventWithDeadLetters,
      event_id: "12121212-1212-1212-1212-121212121212",
      accepted_at: "2026-09-01T10:05:00Z",
    };
    const calls = stubHttp((call) => {
      const { url } = call;
      if (url.pathname.endsWith("/freshness"))
        return {
          status: 200,
          body: { count: url.searchParams.get("watermark") === "wm-1" ? 2 : 0, capped: false },
        };
      if (url.pathname.endsWith("/events")) {
        listReads += 1;
        return listReads === 1
          ? { status: 200, body: { ...page([routedEventWithDeadLetters]), watermark: "wm-1" } }
          : { status: 200, body: { ...page([second, routedEventWithDeadLetters]), watermark: "wm-2" } };
      }
      return respondFor(page([]))(call);
    });

    renderScreen(<EventsScreen tenantId={tenantId} />, `/tenants/${tenantId}/events?status=routed`);

    expect(await screen.findByText("2 new Events since you opened this")).toBeTruthy();
    // The count is read under the ledger's own filters, and the rows under the reader are untouched.
    const poll = calls.find((call) => call.url.pathname.endsWith("/freshness"))!;
    expect(poll.url.searchParams.get("status")).toBe("routed");
    expect(screen.getAllByRole("rowheader")).toHaveLength(1);

    fireEvent.click(screen.getByRole("button", { name: "Show" }));

    await waitFor(() => expect(screen.getAllByRole("rowheader")).toHaveLength(2));
    // Only the first page is read again, from the top.
    expect(eventsCall(calls).at(-1)!.url.searchParams.has("after")).toBe(false);
    await waitFor(() => expect(screen.queryByText(/new Events? since you opened this/)).toBeNull());
    expect(
      calls.some(
        (call) => call.url.pathname.endsWith("/freshness") && call.url.searchParams.get("watermark") === "wm-2",
      ),
    ).toBe(true);
  });

  it("counts new Events under a closed accepted range, both ends included", async () => {
    const calls = stubHttp((call) =>
      call.url.pathname.endsWith("/freshness")
        ? { status: 200, body: { count: 3, capped: false } }
        : call.url.pathname.endsWith("/events")
          ? { status: 200, body: { ...page([routedEventWithDeadLetters]), watermark: "wm-1" } }
          : respondFor(page([]))(call),
    );

    renderScreen(
      <EventsScreen tenantId={tenantId} />,
      `/tenants/${tenantId}/events?accepted_from=2026-09-01T09%3A00%3A00Z&accepted_to=2026-09-01T11%3A00%3A00Z`,
    );

    expect(await screen.findByText("3 new Events since you opened this")).toBeTruthy();
    const poll = calls.find((call) => call.url.pathname.endsWith("/freshness"))!;
    expect(poll.url.searchParams.get("accepted_from")).toBe("2026-09-01T09:00:00Z");
    expect(poll.url.searchParams.get("accepted_to")).toBe("2026-09-01T11:00:00Z");
  });

  it("says so, rather than guessing, when the watermark is refused", async () => {
    stubHttp((call) =>
      call.url.pathname.endsWith("/freshness")
        ? { status: 400, body: { title: "The cursor is invalid or has expired." } }
        : call.url.pathname.endsWith("/events")
          ? { status: 200, body: { ...page([routedEventWithDeadLetters]), watermark: "expired" } }
          : respondFor(page([]))(call),
    );

    renderScreen(<EventsScreen tenantId={tenantId} />);

    expect(await screen.findByText("The ledger can no longer tell what is new since it was read.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Show" })).toBeTruthy();
  });
});

describe("Accepted range pill", () => {
  // The popover it opens is positioned by measuring, so what choosing in it writes is decided in
  // the browser suite; here only what is visible without opening it.
  it("says nothing beyond its label when no range is in force, and states a range that is", async () => {
    stubHttp(respondFor(page([routedEventWithDeadLetters])));
    const { unmount } = renderScreen(<EventsScreen tenantId={tenantId} />, `/tenants/${tenantId}/events`);

    expect((await screen.findByRole("button", { name: "Accepted" })).getAttribute("data-applied")).toBe("false");
    unmount();

    stubHttp(respondFor(page([routedEventWithDeadLetters])));
    renderScreen(
      <EventsScreen tenantId={tenantId} />,
      `/tenants/${tenantId}/events?accepted_from=2026-09-01T09%3A00%3A00Z`,
    );
    const applied = await screen.findByRole("button", { name: /^Accepted from / });
    expect(applied.getAttribute("data-applied")).toBe("true");
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

  it("scopes the ledger to a selected interval and states it in the Accepted pill", async () => {
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

    // The selection lands in the one Accepted control; clearing it is decided in the browser suite.
    expect(screen.getByRole("button", { name: /^Accepted/ }).getAttribute("data-applied")).toBe("true");
    expect(router.state.location.search).toContain("accepted_from=");
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

  it("applies a drag once, on release, rather than once per interval crossed", async () => {
    const calls = stubHttp(respondFor(page([])));

    const { router } = renderScreen(<EventsScreen tenantId={tenantId} />);
    const intervals = await screen.findByRole("group", { name: /Event activity intervals/ });
    const buttons = within(intervals).getAllByRole("button");
    await waitFor(() => expect(eventsCall(calls).length).toBe(1));

    fireEvent.pointerDown(buttons[6]);
    fireEvent.pointerEnter(buttons[7]);
    fireEvent.pointerEnter(buttons[8]);
    // Previewed while dragging, before anything is read.
    expect(buttons[7].getAttribute("aria-pressed")).toBe("true");
    expect(router.state.location.search).toBe("");
    fireEvent.pointerUp(window);
    fireEvent.click(buttons[8]);

    await waitFor(() => expect(router.state.location.search).toContain("accepted_from="));
    await waitFor(() => expect(eventsCall(calls).length).toBe(2));
    expect(eventsCall(calls)[1].url.searchParams.get("accepted_from")).toBe("2026-09-01T09:30:00.000Z");
    expect(eventsCall(calls)[1].url.searchParams.get("accepted_to")).toBe("2026-09-01T09:45:00.000Z");
  });

  it("leaves one history entry for a keyboard extension, however many steps it takes", async () => {
    stubHttp(respondFor(page([])));

    const { router } = renderScreen(<EventsScreen tenantId={tenantId} />);
    const intervals = await screen.findByRole("group", { name: /Event activity intervals/ });
    const buttons = within(intervals).getAllByRole("button");
    fireEvent.click(buttons[10]);
    await waitFor(() => expect(router.state.historyAction).toBe("PUSH"));
    fireEvent.keyDown(buttons[10], { key: "ArrowLeft", shiftKey: true });
    await waitFor(() => expect(router.state.historyAction).toBe("PUSH"));
    fireEvent.keyDown(buttons[9], { key: "ArrowLeft", shiftKey: true });

    await waitFor(() => expect(router.state.historyAction).toBe("REPLACE"));
    expect(new URLSearchParams(router.state.location.search).get("accepted_from")).toBe("2026-09-01T09:40:00.000Z");
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

  describe("across the repeated hour when clocks fall back", () => {
    // 2026-11-01 in New York repeats 01:00-02:00 local: 05:00-06:00Z is EDT, 06:00-07:00Z is EST.
    // A wall clock in that hour names two instants, so the range must never pass through one.
    const zone = process.env.TZ;
    beforeEach(() => {
      process.env.TZ = "America/New_York";
    });
    afterEach(() => {
      process.env.TZ = zone;
    });
    const secondHour = activityOf({}, { start: "2026-11-01T06:00:00Z" });
    const respond = (call: Call) =>
      call.url.pathname.endsWith("/activity") ? { status: 200, body: secondHour } : respondFor(page([]))(call);

    it("scopes the ledger to a selected interval's own instants and shows it selected", async () => {
      const calls = stubHttp(respond);

      renderScreen(<EventsScreen tenantId={tenantId} />);
      const intervals = await screen.findByRole("group", { name: /Event activity intervals/ });
      const tenth = within(intervals).getAllByRole("button")[9];
      fireEvent.click(tenth);

      await waitFor(() =>
        expect(eventsCall(calls).at(-1)?.url.searchParams.get("accepted_from")).toBe("2026-11-01T06:45:00.000Z"),
      );
      expect(eventsCall(calls).at(-1)!.url.searchParams.get("accepted_to")).toBe("2026-11-01T06:50:00.000Z");
      expect(tenth.getAttribute("aria-pressed")).toBe("true");
    });

    it("keeps a link's instants through a load and an unrelated filter change", async () => {
      const calls = stubHttp(respond);

      const { router } = renderScreen(
        <EventsScreen tenantId={tenantId} />,
        `/tenants/${tenantId}/events?accepted_from=2026-11-01T06%3A45%3A00Z&accepted_to=2026-11-01T06%3A50%3A00Z`,
      );
      await waitFor(() =>
        expect(eventsCall(calls).at(-1)?.url.searchParams.get("accepted_from")).toBe("2026-11-01T06:45:00Z"),
      );

      const type = await screen.findByLabelText("Event type");
      fireEvent.change(type, { target: { value: "order.created" } });
      fireEvent.submit(type.closest("form")!);

      await waitFor(() => expect(router.state.location.search).toContain("event_type=order.created"));
      const applied = new URLSearchParams(router.state.location.search);
      expect(applied.get("accepted_from")).toBe("2026-11-01T06:45:00Z");
      expect(applied.get("accepted_to")).toBe("2026-11-01T06:50:00Z");
    });
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

describe("Event inspector actions", () => {
  const unrouted = (overrides: Record<string, unknown>) => ({
    ...detail("succeeded"),
    status: "unrouted",
    topic_id: "99999999-0000-0000-0000-000000000000",
    event_type: "order.refunded",
    event_deliveries: [],
    ...overrides,
  });

  it("offers Create Subscription only while current configuration can still route the Event", async () => {
    stubHttp(respondFor(page([]), unrouted({ unrouted_actionable: true })));
    const actionable = renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    expect(await screen.findByRole("link", { name: "Create Subscription" })).toBeTruthy();
    actionable.unmount();

    stubHttp(respondFor(page([]), unrouted({ unrouted_actionable: false })));
    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    expect(await screen.findByText(/any more, so current configuration can no longer route it\./)).toBeTruthy();
    expect(screen.getByText("order.refunded").tagName).toBe("CODE");
    expect(screen.queryByRole("link", { name: "Create Subscription" })).toBeNull();
  });

  it("explains when current routing has remedied a retained unrouted Event", async () => {
    stubHttp(respondFor(page([]), unrouted({ unrouted_actionable: false, unrouted_has_current_match: true })));

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);

    expect(await screen.findByText(/matching active Subscription now exists/)).toBeTruthy();
    expect(screen.getByText(/retained Event remains unrouted/)).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Create Subscription" })).toBeNull();
  });

  it("names a deleted Topic as the reason a historical-only Event cannot be routed", async () => {
    stubHttp(respondFor(page([]), unrouted({ unrouted_actionable: false, topic_deleted: true })));

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);

    expect(
      await screen.findByText("This Event's Topic has been deleted, so current configuration can no longer route it."),
    ).toBeTruthy();
  });

  it("opens the trace in a new tab without opener access, only when the deployment offers a link", async () => {
    const traceUrl = "https://tracing.example.test/trace/0af7651916cd43dd8448eb211c80319c";
    stubHttp(respondFor(page([]), { ...detail("succeeded"), trace_url: traceUrl }));
    const linked = renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);

    const open = await screen.findByRole("link", { name: "Open trace" });
    expect(open.getAttribute("href")).toBe(traceUrl);
    expect(open.getAttribute("target")).toBe("_blank");
    expect(open.getAttribute("rel")).toContain("noopener");
    linked.unmount();

    stubHttp(respondFor(page([]), { ...detail("succeeded"), trace_url: null }));
    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    expect(await screen.findByRole("button", { name: "Copy trace id" })).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Open trace" })).toBeNull();
  });
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

  it("reads the backlog again after a replay, so Right now and the rail stop counting it", async () => {
    const calls = stubHttp(respondFor(page([]), detail("dead_lettered")));

    renderScreen(<EventsScreen tenantId={tenantId} selectedEventId={eventId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Replay" }));
    const before = calls.filter((call) => call.url.pathname.endsWith("/backlog")).length;
    fireEvent.click(screen.getByRole("button", { name: "Replay this delivery" }));

    await waitFor(() =>
      expect(calls.filter((call) => call.url.pathname.endsWith("/backlog")).length).toBeGreaterThan(before),
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
