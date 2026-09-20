// @vitest-environment node

import { type Browser, chromium, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { activityOf } from "../../src/test/http";

/// What jsdom cannot decide for the Event ledger and its inspector: real layout (whether the
/// inspector sits beside the ledger or below it) and `matchMedia`, which the narrow-width focus
/// move depends on and jsdom does not implement at all.
const tenantId = "11111111-1111-1111-1111-111111111111";
const loadedEventId = "22222222-2222-2222-2222-222222222222";
const unloadedEventId = "33333333-3333-3333-3333-333333333333";

const session = {
  user_id: "44444444-4444-4444-4444-444444444444",
  display_name: "Operator",
  email: null,
  antiforgery_token: "test-token",
  antiforgery_header_name: "X-Integrios-Antiforgery",
  antiforgery_form_field_name: "__antiforgery",
};

const tenant = {
  id: tenantId,
  slug: "acme",
  name: "Acme",
  status: "active",
  environment: null,
  description: null,
  created_at: "2026-09-01T00:00:00Z",
  updated_at: "2026-09-01T00:00:00Z",
};

const listPage = (items: unknown[]) => ({ items, next_cursor: null });

const backlog = {
  awaiting_routing: { count: 1, oldest_at: "2026-09-01T09:30:00Z" },
  unrouted: { count: 0, oldest_at: null },
  dead_lettered_deliveries: { count: 0, oldest_at: null },
};

const activityFor = (range: string | null) =>
  range === "7d"
    ? activityOf(
        { 20: { routed: 40, delivery_dead_lettered: 2 } },
        { range: "7d", bucketMinutes: 360, bucketCount: 28 },
      )
    : activityOf({ 6: { routed: 3 }, 9: { routed: 5, unrouted: 1 } });

const secondEventId = "55555555-5555-5555-5555-555555555555";

const loadedEvent = {
  event_id: loadedEventId,
  source_event_id: "order-1",
  event_type: "order.created",
  status: "routed",
  accepted_at: "2026-09-01T09:30:00Z",
  deliveries: { pending: 0, in_flight: 0, succeeded: 1, dead_lettered: 0 },
};

const secondEvent = {
  event_id: secondEventId,
  source_event_id: "order-2",
  event_type: "order.created",
  status: "routed",
  accepted_at: "2026-09-01T09:35:00Z",
  deliveries: { pending: 0, in_flight: 0, succeeded: 1, dead_lettered: 0 },
};

function eventDetail(eventId: string) {
  return {
    event_id: eventId,
    status: "routed",
    accepted_at: "2026-09-01T09:30:00Z",
    processed_at: "2026-09-01T09:30:01Z",
    failed_at: null,
    trace_id: null,
    event_deliveries: [],
    delivery_attempts: [],
  };
}

/// A ledger row's primary link, located by the route it points at. The acceptance time it renders
/// is formatted for the browser's own locale and is therefore not a stable handle; the route is,
/// and the route is what the row's selection contract is actually about.
const ledgerLink = (scope: Page, id: string) => scope.locator(`a[href^="/tenants/${tenantId}/events/${id}"]`);

/// The Accepted pill's open panel; scoping to it keeps "Clear" and "To" from matching the bar's own
/// "Clear filters" and every label that merely contains those words.
const panel = (scope: Page) => scope.getByRole("dialog", { name: "Accepted range" });

let server: ViteDevServer;
let browser: Browser;
let origin: string;

beforeAll(async () => {
  server = await createServer({ server: { host: "127.0.0.1", port: 0 } });
  await server.listen();
  const address = server.httpServer!.address();
  if (address === null || typeof address === "string") throw new Error("The dev server exposed no port.");
  origin = `http://127.0.0.1:${address.port}`;
  browser = await chromium.launch();
}, 60_000);

afterAll(async () => {
  await browser?.close();
  await server?.close();
});

const deadLetteredDelivery = {
  event_delivery_id: "99999999-9999-9999-9999-999999999999",
  subscription_id: "4d8e-billing-sink",
  destination_id: "88888888-8888-8888-8888-888888888888",
  status: "dead_lettered",
  lifetime_attempt_count: 8,
  retry_cycle_attempt_count: 5,
  deliver_after: null,
  failed_at: "2026-09-01T09:39:50Z",
};

async function openEvents(
  path: string,
  viewport: { width: number; height: number },
  deliveries: unknown[] = [],
  timezoneId?: string,
): Promise<Page> {
  const browserPage = await browser.newPage({ viewport, timezoneId });
  await browserPage.route("**/auth/session", (route) => route.fulfill({ json: session }));
  await browserPage.route(`**/admin/tenants/${tenantId}/events/backlog`, (route) => route.fulfill({ json: backlog }));
  await browserPage.route(`**/admin/tenants/${tenantId}/events/activity*`, (route) =>
    route.fulfill({ json: activityFor(new URL(route.request().url()).searchParams.get("range")) }),
  );
  await browserPage.route(`**/admin/tenants/${tenantId}/events/*/deliveries`, (route) => {
    const eventId = new URL(route.request().url()).pathname.split("/").at(-2)!;
    return route.fulfill({ json: { ...eventDetail(eventId), event_deliveries: deliveries } });
  });
  await browserPage.route(`**/admin/tenants/${tenantId}/events*`, (route) =>
    route.fulfill({ json: listPage([secondEvent, loadedEvent]) }),
  );
  await browserPage.route(`**/admin/tenants/${tenantId}/sources*`, (route) => route.fulfill({ json: listPage([]) }));
  await browserPage.route(`**/admin/tenants/${tenantId}/topics*`, (route) => route.fulfill({ json: listPage([]) }));
  await browserPage.route(`**/admin/tenants/${tenantId}/destinations*`, (route) =>
    route.fulfill({ json: listPage([]) }),
  );
  await browserPage.route(`**/admin/tenants/${tenantId}`, (route) => route.fulfill({ json: tenant }));
  await browserPage.goto(`${origin}${path}`);
  await browserPage.getByRole("heading", { level: 1, name: "Events" }).waitFor();
  return browserPage;
}

describe("The Event ledger and inspector in a real browser", () => {
  it("preserves the filtered ledger while selection follows links, back, forward, and refresh", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 });
    await page.getByRole("button", { name: /Awaiting routing/ }).click();
    const row = ledgerLink(page, loadedEventId);
    // The row carries the ledger's scope, so selecting an Event does not drop the filter beside it.
    await expect
      .poll(() => row.getAttribute("href"))
      .toBe(`/tenants/${tenantId}/events/${loadedEventId}?status=accepted`);
    await row.click();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    expect(await page.getByRole("button", { name: /Awaiting routing/ }).getAttribute("aria-pressed")).toBe("true");
    expect(await row.getAttribute("aria-current")).toBe("page");
    // `aria-current="page"` names the page being viewed, so exactly one destination carries it.
    // Tenants is the ancestor scope of the open Tenant, not the current page; marking it too left
    // two rail destinations selected at once, which says nothing about which scope is open.
    expect(
      await page
        .getByRole("navigation", { name: "Deployment" })
        .getByRole("link", { name: "Tenants" })
        .getAttribute("aria-current"),
    ).toBeNull();
    expect(
      await page
        .getByRole("navigation", { name: "Tenant", exact: true })
        .getByRole("link", { name: "Events" })
        .getAttribute("aria-current"),
    ).toBe("page");
    expect(await page.locator('[data-shell="rail"] a[aria-current="page"]').count()).toBe(1);

    await page.goBack();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor({ state: "hidden" });
    expect(await row.getAttribute("aria-current")).toBeNull();
    expect(await page.getByRole("button", { name: /Awaiting routing/ }).getAttribute("aria-pressed")).toBe("true");
    await page.goForward();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    expect(await row.getAttribute("aria-current")).toBe("page");
    await page.reload();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    expect(await row.getAttribute("aria-current")).toBe("page");
    await page.close();
  }, 60_000);

  /// The panel is one of the two columns, so a panel that sizes to its content decides how far the
  /// page scrolls. Swapping the selection makes it a line of text until the read answers, and the
  /// browser clamps the scroll position to the shorter page and returns it when the content lands —
  /// the Operator is thrown up the list and back. Measured here rather than described, because
  /// nothing else on screen changes and no other test would notice.
  it("keeps the page's height and the reading position while the selection's detail loads", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 620 });
    await ledgerLink(page, loadedEventId).click();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();

    // Hold the detail read open, so the swap's loading state is what the measurements catch.
    let answer = () => {};
    await page.route(`**/admin/tenants/${tenantId}/events/*/deliveries`, async (route) => {
      await new Promise<void>((done) => {
        answer = done;
      });
      return route.fulfill({ json: eventDetail(secondEventId) });
    });

    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    const reading = await page.evaluate(() => ({
      y: Math.round(window.scrollY),
      height: document.documentElement.scrollHeight,
    }));
    expect(reading.y).toBeGreaterThan(0);

    await ledgerLink(page, secondEventId).click();
    await page.getByText("Loading…").waitFor();
    expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBe(reading.height);
    expect(await page.evaluate(() => Math.round(window.scrollY))).toBe(reading.y);

    answer();
    await page.getByRole("heading", { level: 2, name: `Event ${secondEventId}` }).waitFor();
    expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBe(reading.height);
    expect(await page.evaluate(() => Math.round(window.scrollY))).toBe(reading.y);
    await page.close();
  }, 60_000);

  it("moves focus to the inspector heading on selection only when it is not already beside the ledger", async () => {
    const narrow = await openEvents(`/tenants/${tenantId}/events`, { width: 500, height: 900 });
    await ledgerLink(narrow, loadedEventId).click();
    await expect.poll(() => narrow.evaluate(() => document.activeElement?.tagName)).toBe("H2");
    await narrow.close();

    const wide = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 });
    await ledgerLink(wide, loadedEventId).click();
    await wide.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    // The inspector is already visible beside the ledger, so selecting a row leaves focus on the
    // link the Operator just activated rather than moving it.
    expect(await wide.evaluate(() => document.activeElement?.tagName)).toBe("A");
    await wide.close();
  }, 60_000);

  it("moves focus to the new heading, rather than losing it, when switching from one open Event to another", async () => {
    const narrow = await openEvents(`/tenants/${tenantId}/events/${loadedEventId}`, { width: 500, height: 900 });
    await narrow.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();

    await ledgerLink(narrow, secondEventId).click();
    await narrow.getByRole("heading", { level: 2, name: `Event ${secondEventId}` }).waitFor();
    await expect.poll(() => narrow.evaluate(() => document.activeElement?.textContent)).toBe(`Event ${secondEventId}`);
    await narrow.close();
  }, 60_000);

  it("restores the selected Event and its inspector from a direct route, even when that row is not loaded", async () => {
    const detailPage = await openEvents(`/tenants/${tenantId}/events/${unloadedEventId}`, { width: 1280, height: 900 });

    await detailPage.getByRole("heading", { level: 2, name: `Event ${unloadedEventId}` }).waitFor();
    // The ledger itself never loaded this Event, so the inspector's own independent read is what
    // makes the direct link resolve.
    expect(await detailPage.getByRole("link", { name: `Event ${unloadedEventId}` }).count()).toBe(0);
    await detailPage.close();
  }, 60_000);

  it("keeps the recovery action inside the inspector, not past the edge of a table that scrolls", async () => {
    // Replay is the only recovery the platform offers, and a control reachable only by discovering
    // that something inside the inspector scrolls sideways is one an Operator triaging a dead letter
    // will not find. So nothing in the inspector scrolls sideways, and Replay sits inside its edge.
    const wide = await openEvents(`/tenants/${tenantId}/events/${loadedEventId}`, { width: 1512, height: 950 }, [
      deadLetteredDelivery,
    ]);
    await wide.getByRole("button", { name: "Replay" }).waitFor();

    const reach = await wide.evaluate(() => {
      const inspector = document.querySelector('aside[aria-label="Event detail"]')!;
      const replay = [...inspector.querySelectorAll("button")].find((b) => b.textContent?.trim() === "Replay");
      // Only what Replay is actually inside. A JSON body deliberately scrolls sideways rather than
      // wrapping an identifier mid-string; that is content being read, not a control being hidden.
      const scrolls = (() => {
        for (let node = replay?.parentElement; node && node !== inspector; node = node.parentElement)
          if (node.scrollWidth > node.clientWidth + 1) return true;
        return false;
      })();
      if (!replay) return { found: false, inside: false, scrolls };
      return {
        found: true,
        inside: replay.getBoundingClientRect().right <= inspector.getBoundingClientRect().right,
        scrolls,
      };
    });

    expect(reach.found).toBe(true);
    expect(reach.inside).toBe(true);
    expect(reach.scrolls).toBe(false);
    await wide.close();
  });

  it("lays the inspector out beside the ledger above the desktop breakpoint and below it under 900px", async () => {
    const wide = await openEvents(`/tenants/${tenantId}/events/${loadedEventId}`, { width: 1280, height: 900 });
    await wide.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    const wideLayout = await wide.evaluate(
      () => getComputedStyle(document.querySelector("[data-layout=events]")!).flexDirection,
    );
    expect(wideLayout).toBe("row");
    await wide.close();

    const narrow = await openEvents(`/tenants/${tenantId}/events/${loadedEventId}`, { width: 500, height: 900 });
    await narrow.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    const narrowLayout = await narrow.evaluate(
      () => getComputedStyle(document.querySelector("[data-layout=events]")!).flexDirection,
    );
    expect(narrowLayout).toBe("column");
    await narrow.close();
  }, 60_000);
  /// Applying a filter is now a menu interaction, so what it sends and what it writes to the URL
  /// can only be decided where a menu can open. These two moved down from jsdom when the picker
  /// stopped being a native `<select>`.
  it("sends the applied Delivery status as its own parameter and restarts the cursor", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 });

    const read = page.waitForRequest((request) => request.url().includes("delivery_status"));
    await page.getByLabel("Delivery status").click();
    await page.getByRole("option", { name: "Dead-lettered" }).click();

    // There is no Apply: choosing the option is what reads.
    const applied = new URL((await read).url());
    expect(applied.searchParams.get("delivery_status")).toBe("dead_lettered");
    // Event status is a different filter and stays unset by applying this one.
    expect(applied.searchParams.has("status")).toBe(false);
    // A changed filter restarts from the first cursor rather than reusing one issued for the old scope.
    expect(applied.searchParams.has("after")).toBe(false);
    await page.close();
  }, 60_000);

  it("writes an applied filter to the URL under the Admin API's own parameter name", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 });

    await page.getByLabel("Delivery status").click();
    await page.getByRole("option", { name: "Dead-lettered" }).click();

    await page.waitForFunction(() => window.location.search === "?delivery_status=dead_lettered");
    // A filter change is a navigation, so the previous scope is what Back returns to.
    expect(new URL(page.url()).pathname).toBe(`/tenants/${tenantId}/events`);
    await page.close();
  }, 60_000);

  /// The chooser is the one control that must never leave two identity filters applied: both are
  /// exact matches the Admin API ANDs, so an id and a type from different Events would read as an
  /// empty ledger with nothing saying why. Switching carries the typed value and clears the field it
  /// leaves — and the leaving box's blur arrives after that switch, so this is also what proves the
  /// blur cannot put the old parameter back.
  it("switches the find field, carrying the value and leaving one identity filter applied", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 });

    const find = page.getByRole("searchbox", { name: "Source Event id" });
    await find.fill("order-42");
    await find.press("Enter");
    await page.waitForFunction(() => window.location.search === "?source_event_id=order-42");

    // How the field matches is the box's own description, so assistive technology always has it, and
    // it is drawn beside the box on hover and on focus, which a keyboard reaches too. It is out of
    // flow, so the ledger does not move when it appears. The measured region is the whole ledger,
    // which is on screen whether or not the scope matched any rows.
    const said = page.locator(`#${await find.getAttribute("aria-describedby")}`);
    expect(await said.textContent()).toMatch(/Matched exactly, including case/);
    // Not drawn while the pill is neither hovered nor holding focus.
    await page.getByRole("heading", { level: 1, name: "Events" }).click();
    expect(await said.isVisible()).toBe(false);
    const ledgerTop = async () => Math.round((await page.locator('[data-layout="events"]').boundingBox())!.y);
    const before = await ledgerTop();
    await find.hover();
    expect(await said.isVisible()).toBe(true);
    await find.focus();
    expect(await said.isVisible()).toBe(true);
    expect(await ledgerTop()).toBe(before);

    await page.getByRole("combobox", { name: "Find an Event by" }).click();
    await page.getByRole("option", { name: "Event type" }).click();

    await page.waitForFunction(() => window.location.search === "?event_type=order-42");
    const applied = new URLSearchParams(new URL(page.url()).search);
    expect(applied.get("source_event_id")).toBeNull();
    expect(await page.getByRole("searchbox", { name: "Event type" }).inputValue()).toBe("order-42");

    // Switching with a value typed but not committed: opening the menu blurs the box, which commits
    // under the field being left, and the switch must still end with one parameter, not two. The
    // switch carries what the screen has applied, so wait for the read the commit issues: React
    // adopts the committed value a render after the URL, and the menu can be picked before that.
    await page.getByRole("searchbox", { name: "Event type" }).fill("order.placed");
    const committed = page.waitForRequest(
      (request) => new URL(request.url()).searchParams.get("event_type") === "order.placed",
    );
    await page.getByRole("combobox", { name: "Find an Event by" }).click();
    await committed;
    await page.getByRole("option", { name: "Source Event id" }).click();

    await page.waitForFunction(() => window.location.search === "?source_event_id=order.placed");
    await page.close();
  }, 60_000);

  it("drags across Activity intervals to scope the ledger to their accepted range", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 });
    const intervals = page.getByRole("group", { name: /Event activity intervals/ }).getByRole("button");
    const first = (await intervals.nth(6).boundingBox())!;
    const last = (await intervals.nth(9).boundingBox())!;
    await page.mouse.move(first.x + first.width / 2, first.y + first.height / 2);
    await page.mouse.down();
    await page.mouse.move(last.x + last.width / 2, last.y + last.height / 2, { steps: 8 });
    await page.mouse.up();

    await expect.poll(() => new URL(page.url()).searchParams.get("accepted_from")).toBe("2026-09-01T09:30:00.000Z");
    expect(new URL(page.url()).searchParams.get("accepted_to")).toBe("2026-09-01T09:50:00.000Z");
    for (const index of [6, 7, 8, 9])
      await expect.poll(() => intervals.nth(index).getAttribute("aria-pressed")).toBe("true");
    expect(await intervals.nth(10).getAttribute("aria-pressed")).toBe("false");
    // A drag leaves the keyboard path intact: Enter on one interval selects it alone.
    await intervals.nth(2).focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => new URL(page.url()).searchParams.get("accepted_from")).toBe("2026-09-01T09:10:00.000Z");
    await page.getByRole("button", { name: /^Accepted/ }).click();
    await panel(page).getByRole("button", { name: "Clear" }).click();
    await expect.poll(() => new URL(page.url()).searchParams.has("accepted_from")).toBe(false);
    await page.close();
  }, 60_000);

  describe("Accepted range pill", () => {
    const accepted = (page: Page) => {
      const url = new URL(page.url());
      return [url.searchParams.get("accepted_from"), url.searchParams.get("accepted_to")];
    };

    it("writes each preset as fixed instants and keeps the rest of the scope", async () => {
      const page = await openEvents(`/tenants/${tenantId}/events?status=routed`, { width: 1280, height: 900 });
      for (const [label, minutes] of [
        ["Last hour", 60],
        ["Last 24 h", 24 * 60],
        ["Last 7 d", 7 * 24 * 60],
      ] as const) {
        await page.getByRole("button", { name: /^Accepted/ }).click();
        await panel(page).getByRole("button", { name: label }).click();
        await expect.poll(() => accepted(page)[1]).not.toBeNull();
        const [from, to] = accepted(page);
        expect(new Date(to!).getTime() - new Date(from!).getTime()).toBe(minutes * 60_000);
        expect(new URL(page.url()).searchParams.get("status")).toBe("routed");
        await page.getByRole("button", { name: /^Accepted/ }).click();
        await panel(page).getByRole("button", { name: "Clear" }).click();
        await expect.poll(() => accepted(page)).toEqual([null, null]);
      }
      await page.close();
    }, 60_000);

    it("commits a Custom range's two ends together on Apply and on Enter, and discards a draft on leaving", async () => {
      const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 }, [], "UTC");
      await page.getByRole("button", { name: /^Accepted/ }).click();
      await panel(page).getByLabel("From").fill("2026-09-01T09:00");
      await panel(page).getByLabel("To", { exact: true }).fill("2026-09-01T10:00");
      // Nothing is read until the Operator applies, so a half-open range is never in the URL.
      expect(accepted(page)).toEqual([null, null]);
      await panel(page).getByRole("button", { name: "Apply" }).click();
      await expect.poll(() => accepted(page)).toEqual(["2026-09-01T09:00:00.000Z", "2026-09-01T10:00:00.000Z"]);
      await expect
        .poll(() => page.getByRole("button", { name: /^Accepted/ }).getAttribute("title"))
        .toBe("2026-09-01T09:00:00.000Z – 2026-09-01T10:00:00.000Z");

      // Enter is the same act from the keyboard.
      await page.getByRole("button", { name: /^Accepted/ }).click();
      await panel(page).getByLabel("To", { exact: true }).fill("2026-09-01T11:00");
      await panel(page).getByLabel("To", { exact: true }).press("Enter");
      await expect.poll(() => accepted(page)).toEqual(["2026-09-01T09:00:00.000Z", "2026-09-01T11:00:00.000Z"]);

      // Leaving the panel is not applying: the range in force is the one that was applied.
      await page.getByRole("button", { name: /^Accepted/ }).click();
      await panel(page).getByLabel("To", { exact: true }).fill("2026-09-01T23:00");
      await page.keyboard.press("Escape");
      await expect.poll(() => accepted(page)).toEqual(["2026-09-01T09:00:00.000Z", "2026-09-01T11:00:00.000Z"]);
      await page.close();
    }, 60_000);

    it("keeps an unedited bound's instant inside the repeated fall-back hour", async () => {
      const page = await openEvents(
        `/tenants/${tenantId}/events?accepted_from=2026-11-01T06%3A45%3A00Z&accepted_to=2026-11-01T06%3A50%3A00Z`,
        { width: 1280, height: 900 },
        [],
        "America/New_York",
      );
      await page.getByRole("button", { name: /^Accepted/ }).click();
      await panel(page).getByLabel("To", { exact: true }).fill("2026-11-01T03:00");
      await panel(page).getByLabel("To", { exact: true }).press("Enter");

      // 06:45Z is 01:45 EST, whose wall clock also names 05:45Z; the untouched bound keeps its instant.
      await expect.poll(() => accepted(page)[1]).toBe("2026-11-01T08:00:00.000Z");
      expect(accepted(page)[0]).toBe("2026-11-01T06:45:00Z");
      await page.close();
    }, 60_000);
  });

  it("keeps the week of Activity inside its card at 320 CSS pixels, with usable intervals", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 320, height: 900 });
    await page.getByRole("button", { name: "7 d" }).click();
    const intervals = page.getByRole("group", { name: /Event activity intervals/ }).getByRole("button");
    await expect.poll(() => intervals.count()).toBe(28);

    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
    ).toBe(true);
    expect((await intervals.first().boundingBox())!.width).toBeGreaterThanOrEqual(24);
    // The last interval is reachable by keyboard even though it starts scrolled out of view.
    await intervals.nth(27).focus();
    await page.keyboard.press("Home");
    expect(await intervals.first().evaluate((element) => element === document.activeElement)).toBe(true);
    await page.close();
  }, 60_000);
});
