// @vitest-environment node

import { type Browser, chromium, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

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

const summary = {
  events_accepted: 1,
  awaiting_routing: 0,
  unrouted: 0,
  dead_lettered_deliveries: 0,
  window_start: "2026-09-01T09:00:00Z",
  window_end: "2026-09-01T10:00:00Z",
};

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
const ledgerLink = (scope: Page, id: string) => scope.locator(`a[href="/tenants/${tenantId}/events/${id}"]`);

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

async function openEvents(path: string, viewport: { width: number; height: number }): Promise<Page> {
  const browserPage = await browser.newPage({ viewport });
  await browserPage.route("**/auth/session", (route) => route.fulfill({ json: session }));
  await browserPage.route(`**/admin/tenants/${tenantId}/events/activity-summary*`, (route) =>
    route.fulfill({ json: summary }),
  );
  await browserPage.route(`**/admin/tenants/${tenantId}/events/*/deliveries`, (route) => {
    const eventId = new URL(route.request().url()).pathname.split("/").at(-2)!;
    return route.fulfill({ json: eventDetail(eventId) });
  });
  await browserPage.route(`**/admin/tenants/${tenantId}/events*`, (route) =>
    route.fulfill({ json: listPage([secondEvent, loadedEvent]) }),
  );
  await browserPage.route(`**/admin/tenants/${tenantId}/sources*`, (route) => route.fulfill({ json: listPage([]) }));
  await browserPage.route(`**/admin/tenants/${tenantId}/topics*`, (route) => route.fulfill({ json: listPage([]) }));
  await browserPage.route(`**/admin/tenants/${tenantId}`, (route) => route.fulfill({ json: tenant }));
  await browserPage.goto(`${origin}${path}`);
  await browserPage.getByRole("heading", { level: 1, name: "Events" }).waitFor();
  return browserPage;
}

describe("The Event ledger and inspector in a real browser", () => {
  it("preserves the filtered ledger while selection follows links, back, forward, and refresh", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 });
    await page.getByRole("button", { name: /Events accepted/ }).click();
    const row = ledgerLink(page, loadedEventId);
    const href = await row.getAttribute("href");
    expect(href).toBe(`/tenants/${tenantId}/events/${loadedEventId}`);
    await row.click();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    expect(await page.getByRole("button", { name: /Events accepted/ }).getAttribute("aria-pressed")).toBe("true");
    expect(await row.getAttribute("aria-current")).toBe("page");
    expect(
      await page
        .getByRole("navigation", { name: "Deployment" })
        .getByRole("link", { name: "Tenants" })
        .getAttribute("aria-current"),
    ).toBe("page");
    expect(
      await page
        .getByRole("navigation", { name: "Tenant", exact: true })
        .getByRole("link", { name: "Events" })
        .getAttribute("aria-current"),
    ).toBe("page");

    await page.goBack();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor({ state: "hidden" });
    expect(await row.getAttribute("aria-current")).toBeNull();
    expect(await page.getByRole("button", { name: /Events accepted/ }).getAttribute("aria-pressed")).toBe("true");
    await page.goForward();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    expect(await row.getAttribute("aria-current")).toBe("page");
    await page.reload();
    await page.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    expect(await row.getAttribute("aria-current")).toBe("page");
    await page.close();
  }, 60_000);

  it("opens an Event by middle-click without navigating the original ledger", async () => {
    const page = await openEvents(`/tenants/${tenantId}/events`, { width: 1280, height: 900 });
    // Context-level routes also answer the new tab's first request.
    await page.context().route("**/auth/session", (route) => route.fulfill({ json: session }));
    await page.context().route("**/admin/**", (route) => {
      const path = new URL(route.request().url()).pathname;
      const body = path.endsWith("/deliveries")
        ? eventDetail(loadedEventId)
        : path.endsWith("/activity-summary")
          ? summary
          : path === `/admin/tenants/${tenantId}`
            ? tenant
            : listPage([loadedEvent]);
      return route.fulfill({ json: body });
    });
    const opened = page.context().waitForEvent("page");
    await ledgerLink(page, loadedEventId).click({ button: "middle" });
    const tab = await opened;
    await tab.getByRole("heading", { level: 2, name: `Event ${loadedEventId}` }).waitFor();
    expect(new URL(page.url()).pathname).toBe(`/tenants/${tenantId}/events`);
    expect(new URL(tab.url()).pathname).toBe(`/tenants/${tenantId}/events/${loadedEventId}`);
    await tab.close();
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
});
