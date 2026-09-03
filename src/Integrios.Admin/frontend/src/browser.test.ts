// @vitest-environment node
import { createRequire } from "node:module";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { chromium, type Browser, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";

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

async function openDashboard(path = "/tenants"): Promise<Page> {
  const page = await browser.newPage();
  await page.route("**/auth/session", (route) => route.fulfill({ json: session }));
  await page.route("**/admin/tenants/*", (route) => route.fulfill({ json: tenants.items[0] }));
  await page.route("**/admin/tenants*", (route) => route.fulfill({ json: tenants }));
  await page.goto(`${origin}${path}`);
  await page.getByRole("heading", { level: 1 }).waitFor();
  return page;
}

async function accessibilityViolations(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: axePath });
  return page.evaluate(async () => {
    const results = await (window as unknown as { axe: { run: Function } }).axe.run(document, {
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
    // for the browser doing the right thing.
    const expected = await page.$$eval(
      "a[href], button, input:not([type=hidden]), select, textarea",
      (elements) =>
        elements
          .filter((element) => !element.hasAttribute("disabled"))
          .map((element) => element.tagName.toLowerCase()),
    );

    const reached: string[] = [];
    const rings: string[] = [];
    for (let step = 0; step < expected.length; step++) {
      await page.keyboard.press("Tab");
      const stop = await page.evaluate(() => {
        const active = document.activeElement as HTMLElement | null;
        if (!active || active === document.body) return null;
        return {
          tag: active.tagName.toLowerCase(),
          // A ring the browser draws for keyboard focus only. Reading it after a scripted
          // .focus() would report nothing, because :focus-visible does not match that.
          ring: active.matches(":focus-visible") ? getComputedStyle(active).outlineStyle : "none",
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

  // Two screens, because they carry different link shapes: the list has navigation and table
  // links, the detail has the capability list. Each is a separate target-size rule.
  it.each([
    ["the list", "/tenants"],
    ["a detail screen", `/tenants/${tenants.items[0].id}`],
  ])("passes the accessibility rules that need real layout on %s", async (_name, path) => {
    const page = await openDashboard(path);
    expect(await accessibilityViolations(page)).toEqual([]);
    await page.close();
  }, 60_000);
});
