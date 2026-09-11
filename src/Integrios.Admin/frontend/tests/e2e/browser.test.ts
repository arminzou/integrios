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

const destination = {
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

  // A cold dev server optimises dependencies and transforms modules on the first page it serves.
  // Paying for that here keeps it out of whichever test happens to run first, which otherwise
  // intermittently exceeds its own timeout under a loaded machine.
  const warm = await browser.newPage();
  await warm.route("**/auth/session", (route) => route.fulfill({ status: 401 }));
  await warm.goto(origin);
  await warm.getByRole("heading", { level: 1 }).waitFor();
  await warm.close();
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
    if (pathname.endsWith("/destinations")) return route.fulfill({ json: { items: [destination], next_cursor: null } });
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
  it.each([
    ["a first arrival", "/", 401],
    ["a remembered deep link", `/tenants/${tenants.items[0].id}/events`, 401],
    ["a completed sign-out", "/?signed_out=1", 401],
    ["a refused sign-in", "/?error=access_denied", 401],
    ["an unreadable session", "/", 503],
  ])("keeps the signed-out Gate accessible for %s", async (_name, path, status) => {
    const page = await browser.newPage({ viewport: { width: 320, height: 900 } });
    await page.route("**/auth/session", (route) => route.fulfill({ status }));
    await page.goto(`${origin}${path}`);

    await page.getByRole("heading", { level: 1 }).waitFor();
    expect(await accessibilityViolations(page)).toEqual([]);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
    ).toBe(true);

    await page.keyboard.press("Tab");
    await page.keyboard.press("Tab");
    const focus = await page.evaluate(() => {
      const active = document.activeElement as HTMLElement;
      return {
        role: active.tagName.toLowerCase(),
        marked: active.matches(":focus-visible") && getComputedStyle(active).boxShadow !== "none",
      };
    });
    expect(["a", "button"]).toContain(focus.role);
    expect(focus.marked).toBe(true);

    await page.close();
  });

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
        // A control whose focus mark is drawn on the box around it rather than on itself — the
        // search filter, whose input sits inside a pill that tints on `focus-within`. Found by that
        // box's own element rather than by a marker attribute, so nothing exists in the shipped DOM
        // only to be selected from here.
        const highlightedBox = active.closest<HTMLElement>("form, label");
        // A mark the browser draws for keyboard focus only. Reading it after a scripted .focus()
        // would report nothing, because :focus-visible does not match that. A control may mark
        // focus with the browser's own outline or, like the vendored primitives, with a drawn ring;
        // what this asserts is that a keyboard Operator can see where they are, not which of the
        // two the control chose.
        const directlyMarked = style.outlineStyle !== "none" || style.boxShadow !== "none";
        let enclosingBoxMarked = false;
        if (!directlyMarked && highlightedBox) {
          const boxStyle = getComputedStyle(highlightedBox);
          const resolvedColor = (token: string) => {
            const probe = document.createElement("span");
            probe.style.color = `var(${token})`;
            document.body.append(probe);
            const color = getComputedStyle(probe).color;
            probe.remove();
            return color;
          };
          enclosingBoxMarked = boxStyle.backgroundColor === resolvedColor("--selected-surface");
        }
        return {
          tag: active.tagName.toLowerCase(),
          ring: active.matches(":focus-visible") && (directlyMarked || enclosingBoxMarked) ? "visible" : "none",
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
    [
      "an authoring screen with its create form open",
      `/tenants/${tenants.items[0].id}/destinations`,
      "New Destination",
    ],
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
    const wideRail = await wide.locator("[data-shell=rail]").boundingBox();
    const wideMain = await wide.locator("#main").boundingBox();
    expect(wideRail).not.toBeNull();
    expect(wideMain).not.toBeNull();
    // Beside: the document starts after the rail ends horizontally, and they share vertical space.
    expect(wideMain!.x).toBeGreaterThanOrEqual(wideRail!.x + wideRail!.width);
    expect(wideMain!.y).toBeLessThan(wideRail!.y + wideRail!.height);
    await wide.close();

    const narrow = await openDashboard("/tenants", { viewport: { width: 320, height: 900 } });
    const narrowRail = await narrow.locator("[data-shell=rail]").boundingBox();
    const narrowMain = await narrow.locator("#main").boundingBox();
    // Above: the document starts below the band, and the band spans the full width.
    expect(narrowMain!.y).toBeGreaterThanOrEqual(narrowRail!.y + narrowRail!.height);
    expect(narrowRail!.width).toBeGreaterThan(300);
    await narrow.close();
  });

  it.each([
    ["the list", "/tenants"],
    ["a detail screen", `/tenants/${tenants.items[0].id}`],
    ["an authoring screen", `/tenants/${tenants.items[0].id}/destinations`],
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

  // The authoring flyout changes shape as capabilities are chosen. Whether that costs an Operator
  // their place in the form is only decidable with layout: jsdom has no scroll position to lose.
  it("keeps the Connector authoring flyout operable and in place at 320 CSS pixels", async () => {
    const page = await openDashboard("/connectors", { viewport: { width: 320, height: 900 } });
    await page.getByRole("button", { name: "New Connector" }).click();
    const sheet = page.getByRole("dialog", { name: "New Connector" });
    await sheet.waitFor();

    await page.getByLabel("Name", { exact: true }).fill("GitHub");
    await page.getByLabel("Key", { exact: true }).fill("github");

    const deliver = sheet.getByRole("checkbox", { name: /Deliver Events over HTTP/ });
    await deliver.focus();
    const before = await sheet.evaluate((element) => element.scrollTop);
    await page.keyboard.press("Space");

    await sheet.getByText("Destination and Subscription authoring own the concrete outbound contract.").waitFor();
    expect(await deliver.isChecked()).toBe(true);
    // The section it revealed is rendered into the standing flyout, so neither the scroll position
    // nor the control that opened it moves out from under the Operator.
    expect(await sheet.evaluate((element) => element.scrollTop)).toBe(before);
    expect(await deliver.evaluate((element) => element === document.activeElement)).toBe(true);

    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
    ).toBe(true);
    await page.close();
  }, 60_000);

  // The Builder belongs to webhook and queue Sources. Whether its three columns are readable side
  // by side and stack rather than overflow when narrow is a layout fact, so it is decided here.
  it.each([
    ["side by side on a wide screen", 1512, true],
    ["stacked at 320 CSS pixels", 320, false],
  ])(
    "lays the Integrios Event Builder out %s",
    async (_name, width, beside) => {
      const page = await openDashboard(`/tenants/${tenants.items[0].id}/sources`, { viewport: { width, height: 900 } });
      await page.getByRole("button", { name: "New Source" }).click();
      await page.getByRole("button", { name: "Open Integrios Event Builder" }).click();

      const builder = page.getByRole("dialog", { name: "Integrios Event Builder" });
      await builder.waitFor();
      const boxes = [];
      for (const title of ["Representative request", "Event fields", "Normalized Event"])
        boxes.push((await builder.getByRole("heading", { name: title }).boundingBox())!);

      if (beside) {
        expect(boxes[1].x).toBeGreaterThan(boxes[0].x);
        expect(boxes[2].x).toBeGreaterThan(boxes[1].x);
        // Same row: a pane header carrying a button is a little taller, so they share a band
        // rather than an exact offset.
        expect(Math.abs(boxes[1].y - boxes[0].y)).toBeLessThan(24);
      } else {
        expect(boxes[1].y).toBeGreaterThan(boxes[0].y);
        expect(boxes[2].y).toBeGreaterThan(boxes[1].y);
      }
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
      ).toBe(true);
      await page.close();
    },
    60_000,
  );

  it("inserts an Advanced JSONata suggestion from the keyboard alone", async () => {
    const page = await openDashboard(`/tenants/${tenants.items[0].id}/sources`);
    await page.getByRole("button", { name: "New Source" }).click();
    await page.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    await page.getByRole("button", { name: "Advanced JSONata" }).click();

    const editor = page.getByLabel("Source mapping expression");
    await editor.fill("");
    await editor.type("$ex");
    await page.getByRole("list", { name: "JSONata suggestions" }).waitFor();
    await page.keyboard.press("Enter");

    // The caret is left inside the call it inserted, which is where the argument goes.
    expect(await editor.inputValue()).toBe("$exists()");
    expect(await editor.evaluate((element: HTMLTextAreaElement) => element.selectionStart)).toBe(8);
    await page.close();
  }, 60_000);

  // Native constraint validation and layout are both browser behaviours: jsdom runs neither, so only
  // here can the form's own message be told apart from the browser's bubble, and only here can what
  // that message costs the form be measured.
  it("reports a rejected field over the form, in its own words, without moving anything", async () => {
    const page = await openDashboard("/connectors");
    await page.getByRole("button", { name: "New Connector" }).click();

    // The distance between two controls, which scrolling cannot change.
    const spacing = () =>
      page.evaluate(() => {
        const controls = document.querySelectorAll('[role="dialog"] input');
        return Math.round(
          controls[controls.length - 1].getBoundingClientRect().top - controls[0].getBoundingClientRect().top,
        );
      });
    const before = await spacing();
    await page.getByRole("button", { name: "Create Connector" }).click();

    // Every empty required control is named by the schema.
    for (const message of ["Enter a name.", "Enter a key."]) await page.getByText(message).waitFor();

    expect(await spacing()).toBe(before);
    // Floating is only useful if it floats where the field is: a message positioned against some
    // ancestor other than its own row lands somewhere else entirely, and still passes a text check.
    // Both cases: a field with no hint, and one whose hint line the message hangs from. Anchoring
    // the message to the row instead of that line drops it a whole hint below the control it is
    // about, onto the next field — while every text assertion still passes.
    for (const [label, message] of [
      ["Name", "Enter a name."],
      ["Key", "Enter a key."],
    ]) {
      const control = (await page.getByLabel(label, { exact: true }).boundingBox())!;
      const bubble = (await page.locator('[role="alert"]', { hasText: message }).boundingBox())!;
      // Flush: the arrow rises about 7 pixels above the box, so this is the message's tip resting on
      // the control's own edge rather than floating somewhere under it.
      expect(bubble.y - (control.y + control.height)).toBeLessThan(10);
      expect(Math.abs(bubble.x - control.x)).toBeLessThan(4);
    }
    // The browser refused nothing: the form is what reported the failure.
    expect(
      await page.getByLabel("Name", { exact: true }).evaluate((element: HTMLInputElement) => element.validity.valid),
    ).toBe(false);
    // The screen carries a filter form of its own, so this asks the authoring one.
    expect(await page.evaluate(() => document.querySelector<HTMLFormElement>('[role="dialog"] form')?.noValidate)).toBe(
      true,
    );

    // The message carries the field's hint, and the line that usually holds it keeps its box
    // without being drawn or announced.
    const hint = "Names this Connector in manifests and Source or Destination authoring.";
    const message = page.locator('[role="alert"]', { hasText: "Enter a key." });
    expect(await message.textContent()).toContain(hint);
    const inline = page.locator("[data-slot=form-description]", { hasText: hint });
    expect(await inline.evaluate((element) => getComputedStyle(element).visibility)).toBe("hidden");
    expect(await inline.evaluate((element) => element.getBoundingClientRect().height)).toBeGreaterThan(0);
    await page.close();
  }, 60_000);

  it("answers for its own fields on every authored form, not only the one", async () => {
    const page = await openDashboard("/tenants");
    await page.getByRole("button", { name: "New Tenant" }).click();
    await page.getByRole("button", { name: "Create Tenant" }).click();

    await page.getByText("Enter a slug.").waitFor();
    await page.getByText("Enter a name.").waitFor();
    await page.close();
  }, 60_000);

  // A press has to be distinguishable from a hover, or a control under the pointer looks the same
  // whether or not it is being pressed. Both mechanisms are covered: a filled variant presses from
  // its translucent hover back to full strength, an outlined one from the hover surface down to the
  // selected one. Held rather than clicked — a click resolves before anything can be measured.
  it.each([
    ["a filled button", "/tenants", "New Tenant"],
    ["an outlined button", `/tenants/${tenants.items[0].id}`, "Edit"],
  ])(
    "answers a press distinguishably from a hover on %s",
    async (_name, path, name) => {
      // Reduced motion is what makes each state readable in one sample: the platform rule cuts every
      // transition to an imperceptible step, so a colour is either the old one or the new one and
      // never a value in between. Sampling a running transition would differ from the sample before
      // it whatever the press did, which passes an assertion that only asks for a difference even
      // with no `active` rule present at all. The press itself is a colour, not motion, so removing
      // the animation removes nothing this is measuring.
      const page = await openDashboard(path, { reducedMotion: "reduce" });
      const button = page.getByRole("button", { name });
      const background = () => button.evaluate((element) => getComputedStyle(element).backgroundColor);

      // Polled rather than read straight after the pointer moves: the browser has not necessarily
      // recomputed style by the time the next call arrives. Under reduced motion a poll is safe —
      // with no transition running, the value it reads is one of the discrete states and never a
      // frame between two of them.
      const resting = await background();
      await button.hover();
      await expect.poll(background).not.toBe(resting);
      const hovered = await background();

      await page.mouse.down();
      await expect.poll(background).not.toBe(hovered);
      await page.mouse.up();

      await page.close();
    },
    60_000,
  );

  // The loading cover is the document column and nothing else: it fills `<main>` exactly, and the
  // rail beside it stays uncovered, because navigating away is the one thing worth doing while a
  // slow read is outstanding. Positioned against the wrong ancestor it would either cover the rail
  // too or shrink to the list it was rendered inside, and both still look plausible on a fast local
  // read — so the list response is held open, which is the only way either is observable.
  it("covers the document column while the list is read, and leaves the rail alone", async () => {
    const page = await browser.newPage();
    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    await page.route("**/auth/session", (route) => route.fulfill({ json: session }));
    await page.route("**/admin/**", async (route) => {
      const pathname = new URL(route.request().url()).pathname;
      if (/\/admin\/tenants\/[^/]+$/.test(pathname)) return route.fulfill({ json: tenants.items[0] });
      if (pathname.endsWith("/connectors")) return route.fulfill({ json: { items: [connector], next_cursor: null } });
      if (pathname.endsWith("/destinations")) {
        await held;
        return route.fulfill({ json: { items: [destination], next_cursor: null } });
      }
      return route.fulfill({ json: { items: [], next_cursor: null } });
    });
    await page.goto(`${origin}/tenants/${tenants.items[0].id}/destinations`);

    const cover = page.locator('[role="status"][aria-busy="true"]');
    await cover.waitFor();
    const covered = (await cover.boundingBox())!;
    const main = (await page.locator("main").boundingBox())!;

    // The whole column, to the pixel — and no more than it, which is what leaves the rail alone,
    // because where `<main>` sits relative to the rail is already pinned by the layout test above.
    expect(covered).toEqual(main);

    // Gone once the rows are there, rather than left sitting over them.
    release();
    await page.locator("table").waitFor();
    await expect.poll(() => cover.count()).toBe(0);

    await page.close();
  }, 60_000);

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
