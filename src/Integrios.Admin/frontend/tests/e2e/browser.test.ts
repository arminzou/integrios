// @vitest-environment node
import { createRequire } from "node:module";
import { type Browser, chromium, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

/// What jsdom cannot decide. It implements no sequential focus navigation, so tab order and the
/// focus ring are unprovable there, and it has no layout, so the accessibility rules that measure
/// rendered geometry and colour are switched off in the jsdom pass. Those are exactly the items on
/// the Operator keyboard review, so they are pinned here against a real browser instead.
///
/// The API is answered by the browser rather than by a deployment: these assertions are about how
/// the rendered page behaves, and standing up Admin, a database and an identity provider to decide
/// them would be slower and less controllable without deciding anything more.
const axePath = createRequire(import.meta.url).resolve("axe-core/axe.min.js");

const session = {
  user_id: "11111111-1111-1111-1111-111111111111",
  display_name: "Operator",
  email: "operator@example.test",
  antiforgery_token: "test-token",
  antiforgery_header_name: "X-Integrios-Antiforgery",
  antiforgery_form_field_name: "__antiforgery",
};

const tenants = {
  items: [
    {
      id: "22222222-2222-2222-2222-222222222222",
      slug: "acme",
      name: "Acme",
      status: "active",
      environment: "production",
      description: null,
      created_at: "2026-09-01T00:00:00Z",
      updated_at: "2026-09-01T00:00:00Z",
    },
  ],
  next_cursor: null,
};

const stamps = { created_at: "2026-09-01T00:00:00Z", updated_at: "2026-09-01T00:00:00Z" };

const connector = {
  id: "33333333-3333-3333-3333-333333333333",
  key: "http",
  contract_version: 1,
  name: "HTTP",
  direction: "both",
  status: "active",
  description: null,
  ...stamps,
};

const connection = {
  id: "44444444-4444-4444-4444-444444444444",
  tenant_id: tenants.items[0].id,
  connector_id: connector.id,
  name: "orders-sink",
  status: "active",
  environment: "production",
  description: "Delivers orders to the ERP",
  ...stamps,
};

const summary = {
  window_start: "2026-09-01T12:00:00Z",
  window_end: "2026-09-01T13:00:00Z",
  events_accepted: 128,
  awaiting_routing: 3,
  unrouted: 2,
  dead_lettered_deliveries: 5,
};

const event = {
  event_id: "55555555-5555-5555-5555-555555555555",
  event_type: "order.created",
  source_event_id: "ord-1001",
  status: "routed",
  accepted_at: "2026-09-01T12:45:00Z",
  deliveries: { pending: 0, in_flight: 0, succeeded: 1, dead_lettered: 1 },
};

let server: ViteDevServer;
let browser: Browser;
let origin: string;

beforeAll(async () => {
  // Bind the loopback address explicitly: "localhost" can resolve to ::1, and the
  // browser would then be refused on 127.0.0.1.
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

async function openDashboard(path = "/tenants", options: Parameters<Browser["newPage"]>[0] = {}): Promise<Page> {
  const page = await browser.newPage(options);
  await page.route("**/auth/session", (route) => route.fulfill({ json: session }));
  await page.route("**/admin/**", (route) => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname.endsWith("/activity-summary")) return route.fulfill({ json: summary });
    if (pathname.endsWith("/connectors")) return route.fulfill({ json: { items: [connector], next_cursor: null } });
    if (/\/admin\/tenants\/[^/]+$/.test(pathname)) return route.fulfill({ json: tenants.items[0] });
    if (pathname.endsWith("/admin/tenants")) return route.fulfill({ json: tenants });
    if (pathname.endsWith("/connections")) return route.fulfill({ json: { items: [connection], next_cursor: null } });
    if (pathname.endsWith("/events")) return route.fulfill({ json: { items: [event], next_cursor: null } });
    return route.fulfill({ json: { items: [], next_cursor: null } });
  });
  await page.goto(`${origin}${path}`);
  await page.getByRole("heading", { level: 1 }).waitFor();
  return page;
}

async function accessibilityViolations(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: axePath });
  return page.evaluate(async () => {
    const results = await (
      window as unknown as { axe: { run: (root: Document, options: unknown) => Promise<unknown> } }
    ).axe.run(document, {
      runOnly: { type: "rule", values: ["color-contrast", "target-size"] },
    });
    return (results as { violations: { id: string; nodes: { html: string }[] }[] }).violations.map(
      (violation) => `${violation.id}: ${violation.nodes.map((node) => node.html).join(" | ")}`,
    );
  });
}

describe("The dashboard in a real browser", () => {
  it("reaches every control by keyboard, in reading order, with a visible focus ring", async () => {
    const page = await openDashboard();

    // Document order, focusable elements only. A hidden input — the antiforgery field the sign-out
    // form carries — is in the DOM and is not a tab stop, so counting it here would fail the test
    // for the browser doing the right thing. A closed disclosure's own content is the same case:
    // still in the DOM, but not reachable until its <summary> is activated, so only the summary
    // itself is a stop. A create panel that is rendered but `hidden` is the same case again: it
    // stays in the DOM so `aria-controls` on its trigger resolves, and it is not focusable until
    // the trigger opens it.
    const expected = await page.$$eval(
      "a[href], button, input:not([type=hidden]), select, textarea, summary",
      (elements) =>
        elements
          .filter((element) => !element.hasAttribute("disabled"))
          .filter((element) => element.closest("[hidden]") === null)
          .filter((element) => {
            const closedAncestor = element.closest("details:not([open])");
            if (!closedAncestor) return true;
            // The summary belonging to a closed disclosure is still how a keyboard Operator opens
            // it, so it stays a real tab stop; everything else inside is not, until it is open.
            return element.tagName === "SUMMARY" && element.parentElement === closedAncestor;
          })
          .map((element) => element.tagName.toLowerCase()),
    );

    const reached: string[] = [];
    const rings: string[] = [];
    for (let step = 0; step < expected.length; step++) {
      await page.keyboard.press("Tab");
      const stop = await page.evaluate(() => {
        const active = document.activeElement as HTMLElement | null;
        if (!active || active === document.body) return null;
        const style = getComputedStyle(active);
        // A mark the browser draws for keyboard focus only. Reading it after a scripted .focus()
        // would report nothing, because :focus-visible does not match that. A control may mark
        // focus with the browser's own outline or, like the vendored primitives, with a drawn ring;
        // what this asserts is that a keyboard Operator can see where they are, not which of the
        // two the control chose.
        const marked = style.outlineStyle !== "none" || style.boxShadow !== "none";
        return {
          tag: active.tagName.toLowerCase(),
          ring: active.matches(":focus-visible") && marked ? "visible" : "none",
        };
      });
      if (!stop) break;
      reached.push(stop.tag);
      rings.push(stop.ring);
    }

    // Tabbing walks the document in order and skips nothing, so nothing is pointer-only.
    expect(reached).toEqual(expected);
    // Every stop is visibly marked, or a keyboard Operator cannot tell where they are.
    expect(rings.filter((ring) => ring === "none")).toEqual([]);

    await page.close();
  }, 60_000);

  // One case per shape of control the dashboard has: navigation and table links, the capability
  // list, an authoring form, and the Event summary's pressed buttons beside the ledger. Contrast and
  // target size are measured, so each needs real layout rather than jsdom.
  it.each([
    ["the list", "/tenants", null],
    ["a detail screen", `/tenants/${tenants.items[0].id}`, null],
    ["an authoring screen with its create form open", `/tenants/${tenants.items[0].id}/connections`, "New Connection"],
    // The Event ledger's filters are on screen from the start, so there is nothing to open here.
    ["the Event ledger and its activity summary", `/tenants/${tenants.items[0].id}/events`, undefined],
  ])(
    "passes the accessibility rules that need real layout on %s",
    async (_name, path, disclosure) => {
      const page = await openDashboard(path);
      // A collapsed panel carries no violations to find; the form inside it does.
      if (disclosure) await page.click(`text=${disclosure}`);
      expect(await accessibilityViolations(page)).toEqual([]);
      await page.close();
    },
    60_000,
  );

  // 320 CSS pixels is the narrowest width the dashboard supports. A table may scroll inside its own
  // region there; the document itself may not, because a page that slides sideways hides half of
  // itself from an Operator who cannot see it happening.
  // jsdom has no layout, so "beside" and "above" are only decidable here. The rail is the shell's
  // one claim on the viewport, and it has to give the document the width at desktop and the height
  // at narrow — the reverse of either is the failure this change would show up as.
  it("puts the rail beside the document at desktop and above it when narrow", async () => {
    const wide = await openDashboard("/tenants", { viewport: { width: 1512, height: 900 } });
    const wideRail = await wide.locator(".rail").boundingBox();
    const wideMain = await wide.locator("#main").boundingBox();
    expect(wideRail).not.toBeNull();
    expect(wideMain).not.toBeNull();
    // Beside: the document starts after the rail ends horizontally, and they share vertical space.
    expect(wideMain!.x).toBeGreaterThanOrEqual(wideRail!.x + wideRail!.width);
    expect(wideMain!.y).toBeLessThan(wideRail!.y + wideRail!.height);
    await wide.close();

    const narrow = await openDashboard("/tenants", { viewport: { width: 320, height: 900 } });
    const narrowRail = await narrow.locator(".rail").boundingBox();
    const narrowMain = await narrow.locator("#main").boundingBox();
    // Above: the document starts below the band, and the band spans the full width.
    expect(narrowMain!.y).toBeGreaterThanOrEqual(narrowRail!.y + narrowRail!.height);
    expect(narrowRail!.width).toBeGreaterThan(300);
    await narrow.close();
  });

  it.each([
    ["the list", "/tenants"],
    ["a detail screen", `/tenants/${tenants.items[0].id}`],
    ["an authoring screen", `/tenants/${tenants.items[0].id}/connections`],
    ["the Event ledger", `/tenants/${tenants.items[0].id}/events`],
  ])(
    "has no horizontal document overflow at 320 CSS pixels on %s",
    async (_name, path) => {
      const page = await openDashboard(path, { viewport: { width: 320, height: 900 } });

      const document_ = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        clientWidth: document.documentElement.clientWidth,
      }));

      expect(document_.scrollWidth).toBeLessThanOrEqual(document_.clientWidth);
      await page.close();
    },
    60_000,
  );

  it("removes the primitives' motion for an Operator who asked the platform for less of it", async () => {
    const page = await openDashboard("/tenants", { reducedMotion: "reduce" });

    // Every control the shell and the list render, not a sample: a transition left in place here is
    // one an Operator asked not to be shown.
    const durations = await page.$$eval("a[href], button, input, select, textarea, summary, tr", (elements) =>
      elements.map((element) => getComputedStyle(element).transitionDuration),
    );

    expect(durations.length).toBeGreaterThan(0);
    expect(durations.filter((duration) => Number.parseFloat(duration) > 0.001)).toEqual([]);
    await page.close();
  }, 60_000);
});
