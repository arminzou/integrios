// @vitest-environment node

import { createRequire } from "node:module";
import { type Browser, chromium, type Locator, type Page, type Request } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

/// Every create form, filled and submitted through a real browser.
///
/// The jsdom tests dispatch React's synthetic events directly, and hand-driving the page through a
/// devtools protocol sets `.value` without React ever seeing it — neither exercises what a person
/// typing into the form actually produces. Playwright's fill and a real listbox interaction do,
/// which is why the
/// request these assertions inspect is the request the API would really receive.
///
/// What they check is the part the type system cannot: the coercions between a form, which holds
/// only strings, and a JSON body with numbers, objects, and meaningful nulls.
const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const destinationId = "33333333-3333-3333-3333-333333333333";
const connectorId = "44444444-4444-4444-4444-444444444444";

const session = {
  user_id: "55555555-5555-5555-5555-555555555555",
  display_name: "Operator",
  email: null,
  antiforgery_token: "test-token",
  antiforgery_header_name: "X-Integrios-Antiforgery",
  antiforgery_form_field_name: "__antiforgery",
};

const stamps = { created_at: "2026-09-01T00:00:00Z", updated_at: "2026-09-01T00:00:00Z" };
const tenant = {
  id: tenantId,
  slug: "acme",
  name: "Acme",
  status: "active",
  environment: null,
  description: null,
  ...stamps,
};
const topic = {
  id: topicId,
  tenant_id: tenantId,
  key: "orders",
  name: "Orders",
  status: "active",
  description: null,
  ...stamps,
};
const destination = {
  id: destinationId,
  tenant_id: tenantId,
  connector_id: connectorId,
  name: "sink",
  status: "active",
  environment: null,
  description: null,
  ...stamps,
};
const connector = {
  id: connectorId,
  key: "http",
  contract_version: 1,
  name: "HTTP",
  direction: "both",
  status: "active",
  description: null,
  ...stamps,
};
const connectorDetail = {
  ...connector,
  manifest_schema_version: 1,
  manifest: {},
};

const page = (items: unknown[], nextCursor: string | null = null) => ({ items, next_cursor: nextCursor });

const subscriptionId = "77777777-7777-7777-7777-777777777777";
const sourceId = "88888888-8888-8888-8888-888888888888";
const eventId = "99999999-9999-9999-9999-999999999999";

const destinationDetail = {
  ...destination,
  config: { base_uri: "http://sink.invalid" },
  authentication: null,
};
const subscriptionDetail = {
  id: subscriptionId,
  topic_id: topicId,
  tenant_id: tenantId,
  name: "to-sink",
  match_rules: { event_type: "order.created" },
  destination_id: destinationId,
  mapping_config: {
    engine: "jsonata",
    version: "1",
    expression: '{ "order": orderId, "total": total, "placed_at": placedAt }',
  },
  http_delivery: { version: 1, method: "POST", path: null, headers: {}, body: "json" },
  status: "active",
  order_index: 1,
  description: null,
  ...stamps,
};
const sourceDetail = {
  id: sourceId,
  tenant_id: tenantId,
  connector_id: connectorId,
  topic_id: topicId,
  type: "event_api",
  configuration: {},
  status: "active",
  revoked_at: null,
  ...stamps,
};

/// One handler for every screen: reads answer from the path, writes are captured and accepted.
/// Per-endpoint stubs would be a fixture per screen for no extra coverage. Detail routes are
/// matched before their lists, because a list path is a prefix of the detail path under it.
function readFor(pathname: string): unknown {
  if (/\/connectors\/[^/]+$/.test(pathname)) return connectorDetail;
  if (pathname === "/admin/connectors") return page([connector]);
  if (pathname === "/admin/tenants") return page([tenant]);
  if (/\/overview$/.test(pathname))
    return {
      topics: 1,
      destinations: 1,
      sources: 1,
      subscriptions: 1,
      live_api_keys: 1,
      dead_lettered_deliveries: 0,
      ingestion_endpoint: "http://localhost:5231/",
    };
  if (/^\/admin\/tenants\/[^/]+$/.test(pathname)) return tenant;
  if (/\/destinations\/[^/]+$/.test(pathname)) return destinationDetail;
  if (/\/subscriptions\/[^/]+$/.test(pathname)) return subscriptionDetail;
  if (/\/events\/[^/]+\/deliveries$/.test(pathname))
    return {
      event_id: eventId,
      tenant_id: tenantId,
      topic_id: topicId,
      event_type: "order.created",
      status: "routed",
      accepted_at: "2026-09-08T12:00:00Z",
      payload: { orderId: "SO-4014", total: 42, customer: { id: "C-14" }, "placed-at": "today" },
      event_deliveries: [],
      delivery_attempts: [],
    };
  if (/\/sources\/[^/]+$/.test(pathname)) return { ...sourceDetail, id: pathname.split("/").at(-1) };
  if (/\/topics\/[^/]+$/.test(pathname)) return topic;
  if (/\/destinations$/.test(pathname)) return page([destination]);
  // A next_cursor here is what makes the option reads' own hundred-row cap observable.
  if (/\/topics$/.test(pathname)) return page([topic], "more-topics");
  if (/\/subscriptions$/.test(pathname))
    return page([{ ...subscriptionDetail, topic_name: topic.name, destination_name: destination.name }]);
  if (/\/events$/.test(pathname))
    return page([
      {
        event_id: eventId,
        topic_id: topicId,
        event_type: "order.created",
        status: "routed",
        accepted_at: "2026-09-08T12:00:00Z",
        deliveries: { pending: 0, in_flight: 0, succeeded: 1, dead_lettered: 0 },
      },
    ]);
  if (/\/sources$/.test(pathname)) return page([sourceDetail]);
  return page([]);
}

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

async function open(path: string): Promise<{ page: Page; writes: Request[] }> {
  const browserPage = await browser.newPage();
  const writes: Request[] = [];

  await browserPage.route("**/auth/session", (route) => route.fulfill({ json: session }));
  await browserPage.route("**/admin/**", (route) => {
    const request = route.request();
    if (request.method() === "GET") return route.fulfill({ json: readFor(new URL(request.url()).pathname) });
    writes.push(request);
    if (new URL(request.url()).pathname === "/admin/transform/preview")
      return route.fulfill({ json: { output: { order: "SO-4014" } } });
    return route.fulfill({ status: 201, json: { id: "66666666-6666-6666-6666-666666666666", ...stamps } });
  });

  await browserPage.goto(`${origin}${path}`);
  await browserPage.getByRole("heading", { level: 1 }).waitFor();
  return { page: browserPage, writes };
}

it("applies the list filters through their controls and keeps them usable at 320px", async () => {
  const { page: view } = await open("/tenants");
  try {
    await view.getByLabel("Name or slug", { exact: true }).fill("acme");
    await view.getByLabel("Name or slug", { exact: true }).press("Enter");
    await view.waitForURL("**/tenants?name=acme");
    await view.getByLabel("Environment", { exact: true }).fill("production");
    await view.getByLabel("Environment", { exact: true }).press("Enter");
    await view.waitForURL("**environment=production");
    await view.setViewportSize({ width: 320, height: 900 });
    expect(await view.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await view.goto(`${origin}/tenants/${tenantId}/sources`);
    await view.getByText("event_api", { exact: true }).waitFor();
    const request = view.waitForRequest((request) => new URL(request.url()).searchParams.get("topic_id") === topicId);
    await view.getByLabel("Topic", { exact: true }).click();
    // The real browser owns this positioned popup; this assertion proves the same limit described
    // by the trigger is also visible where a sighted Operator chooses an option.
    await view.getByRole("listbox").getByText("Showing the first 100 Topics.").waitFor();
    await view.getByRole("option", { name: "Orders" }).click();
    expect(new URL((await request).url()).searchParams.has("after")).toBe(false);
    await view.waitForURL(`**topic_id=${topicId}`);
    expect(await view.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  } finally {
    await view.close();
  }
}, 60_000);

const axePath = createRequire(import.meta.url).resolve("axe-core/axe.min.js");

/// An authoring sheet is fixed-position, so a form wider than the screen never widens the document
/// and the page-level overflow check cannot see it. Each sheet is therefore opened at 320 CSS pixels
/// and its own form must fit the viewport, with every control meeting the target-size rule.
it.each([
  ["New Destination", `/tenants/${tenantId}/destinations`, "New Destination"],
  ["New Connector", "/connectors", "New Connector"],
  ["Subscription edit", `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`, "Edit"],
])(
  "fits the %s sheet into 320px",
  async (_name, path, trigger) => {
    const { page: view } = await open(path);
    try {
      await view.setViewportSize({ width: 320, height: 900 });
      await view.getByRole("button", { name: trigger, exact: true }).click();
      const sheet = view.getByRole("dialog");
      await sheet.locator("form").first().waitFor();

      const fit = await sheet.evaluate((element) => {
        const form = element.querySelector("form")!.getBoundingClientRect();
        return {
          formRight: form.right,
          viewport: window.innerWidth,
          scrolls: element.scrollWidth > element.clientWidth,
        };
      });
      expect(fit.formRight).toBeLessThanOrEqual(fit.viewport);
      expect(fit.scrolls).toBe(false);

      await view.addScriptTag({ path: axePath });
      const violations = await sheet.evaluate(async (element) => {
        const results = await (
          window as unknown as { axe: { run: (root: Element, options: unknown) => Promise<unknown> } }
        ).axe.run(element, { runOnly: { type: "rule", values: ["target-size"] } });
        return (results as { violations: { id: string; nodes: { html: string }[] }[] }).violations.map(
          (violation) => `${violation.id}: ${violation.nodes.map((node) => node.html).join(" | ")}`,
        );
      });
      expect(violations).toEqual([]);

      // axe excuses an undersized target when enough space surrounds it, so the dashboard's own
      // 24-by-24 minimum is measured directly on every button and link the sheet renders.
      const undersized = await sheet.evaluate((element) =>
        [...element.querySelectorAll("button, a[href]")]
          .filter((control) => {
            const box = control.getBoundingClientRect();
            return box.width > 0 && (box.width < 24 || box.height < 24);
          })
          .map((control) => control.outerHTML.slice(0, 120)),
      );
      expect(undersized).toEqual([]);
    } finally {
      await view.close();
    }
  },
  60_000,
);

/// The dashboard is light-only, and `color-scheme: light` settles only what the browser paints, not
/// what Tailwind's `dark:` variant matches — that answers to the operating system unless it is bound
/// to something else. Every vendored shadcn primitive arrives carrying `dark:` utilities, so this
/// asserts the binding rather than the absence of any one of them: an Operator whose system is dark
/// gets the light theme these fields are drawn for, with no fill behind them.
it("leaves an edit form's fields unfilled for an Operator whose system is in dark mode", async () => {
  const { page: view } = await open(`/tenants/${tenantId}`);
  try {
    await view.emulateMedia({ colorScheme: "dark" });
    await view.getByRole("button", { name: "Edit", exact: true }).click();
    // One field is the whole assertion: what is bound here is the `dark:` variant itself, once, for
    // every utility in the build — so a second field would re-measure the same binding.
    const control = view.getByRole("dialog", { name: "Edit" }).getByLabel("Name", { exact: true });
    await control.waitFor();
    expect(await control.evaluate((field) => getComputedStyle(field).backgroundColor)).toBe("rgba(0, 0, 0, 0)");
  } finally {
    await view.close();
  }
}, 60_000);

/// A screen can carry more than one form — a Topic page holds the Topic's own fields and the create
/// panel for its Subscriptions — and both use the same field names. Controls are therefore addressed
/// by label within the form that owns them, which is also what asserts the label association.
/// A form is addressed by its accessible name. The create forms live inside a sheet that carries the
/// title, and the edit forms behind a disclosure that carries theirs, so neither repeats a heading
/// on screen — the name is stated on the form itself instead of inferred from text near it.
function formNamed(view: Page, name: string) {
  return view.getByRole("form", { name });
}

/// The pickers are a scripted listbox rather than a `<select>`, so there is no `selectOption` to
/// call: a person opens the control and presses the option, and so does this.
async function choose(control: Locator, option: string | RegExp) {
  await control.click();
  await control.page().getByRole("option", { name: option }).click();
}

async function submitted(
  writes: Request[],
): Promise<{ method: string; pathname: string; body: Record<string, unknown> }> {
  const request = writes.find((candidate) => new URL(candidate.url()).pathname !== "/admin/transform/preview");
  expect(request, "The form submitted no request at all.").toBeDefined();
  if (!request) throw new Error("The form submitted no request at all.");
  return {
    method: request.method(),
    pathname: new URL(request.url()).pathname,
    body: request.postDataJSON() as Record<string, unknown>,
  };
}

describe("Create forms, filled through a real browser", () => {
  /// Moved down from jsdom when the Connector picker became a menu: reaching a rejected write means
  /// submitting a valid Destination first, and choosing a Connector needs a layout engine.
  it("puts each rejected field beside its own control and everything else at form level", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/destinations`);
    // Registered after the helper's own handler, so this is the one that answers the write.
    await view.route("**/admin/**", (route) => {
      const request = route.request();
      if (request.method() === "GET") return route.fallback();
      return route.fulfill({
        status: 400,
        contentType: "application/problem+json",
        json: {
          title: "The Destination was rejected.",
          errors: { Name: ["That name is already taken."], "": ["The deployment refused the write."] },
        },
      });
    });

    await view.click("text=New Destination");
    const form = formNamed(view, "Create a Destination");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await form.getByLabel("Name").fill("sink");
    await form.getByLabel("Configuration (JSON)", { exact: true }).fill('{"base_uri":"http://sink.invalid"}');
    await view.click("text=Create Destination");

    // The field-keyed message lands on the control it names, whatever casing the server used.
    const name = form.getByLabel("Name");
    await expect.poll(() => name.getAttribute("aria-invalid")).toBe("true");
    await view.getByText("That name is already taken.").waitFor();
    // What is attributed to no rendered field is still said, at the level of the form.
    await view.getByText("The deployment refused the write.").waitFor();
    await view.close();
  }, 60_000);

  it("sends a Destination with its config parsed out of the textarea", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/destinations`);

    // The create form sits behind a "New Destination" disclosure so it does not permanently
    // dominate the list above it.
    await view.click("text=New Destination");
    const form = formNamed(view, "Create a Destination");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await form.getByLabel("Name").fill("sink");
    await form.getByLabel("Configuration (JSON)", { exact: true }).fill('{"base_uri":"http://sink.invalid"}');
    await view.click("text=Create Destination");
    await view.waitForFunction(() => true);

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/destinations`);
    expect(writes[0].headers()[session.antiforgery_header_name.toLowerCase()]).toBe(session.antiforgery_token);
    expect(sent.body.connector_id).toBe(connectorId);
    // The textarea holds text; the API takes a document.
    expect(sent.body.configuration).toEqual({ base_uri: "http://sink.invalid" });
    expect(sent.body.authentication).toBeNull();
    await view.close();
  }, 60_000);

  it("sends a Topic, leaving an untouched optional field null rather than empty", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/topics`);

    await view.click("text=New Topic");
    // A Topic authors its key; the label and description are the optional fields left untouched.
    await formNamed(view, "Create a Topic").getByLabel("Key", { exact: true }).fill("orders");
    await view.click("text=Create Topic");

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/topics`);
    expect(sent.body.key).toBe("orders");
    expect(sent.body.name).toBeNull();
    expect(sent.body.description).toBeNull();
    await view.close();
  }, 60_000);

  it("sends a Subscription with an Event type and a fixed mapping envelope", async () => {
    // Opening management from a Topic carries the Topic as the list filter. New Subscription reuses
    // that selection, while the write itself remains on the Topic-owned API route.
    const { page: view, writes } = await open(`/tenants/${tenantId}/subscriptions?topic_id=${topicId}`);

    await view.click("text=New Subscription");
    expect(await view.getByRole("dialog", { name: "New Subscription" }).getByLabel("Topic").textContent()).toContain(
      "Orders",
    );
    const form = formNamed(view, "Create a Subscription");
    await form.getByLabel("Name").fill("to-sink");
    await choose(form.getByLabel("Destination"), /sink/);
    await form.getByLabel("Event type").fill("order.created");
    expect(await form.getByLabel("Order").count()).toBe(0);
    expect(await form.getByLabel("Match rules (JSON)").count()).toBe(0);
    expect(await form.getByLabel("Mapping expression (optional)").count()).toBe(0);
    const create = view.locator('form[aria-label="Create a Subscription"] button[type="submit"]');
    await form.getByRole("button", { name: "Add mapping in Playground" }).click();
    const playground = view.getByRole("dialog", { name: "Mapping Playground" });
    await playground.getByLabel("Output field 1").fill("order");
    await playground.getByLabel("Event field 1").selectOption("orderId");
    expect(await create.isDisabled()).toBe(true);
    await playground.getByRole("button", { name: "Preview mapping" }).click();
    await playground.getByText('"order": "SO-4014"').waitFor();
    await playground.getByRole("button", { name: "Confirm mapping change" }).click();
    await form.getByText("Mapping change reviewed and ready to save.").waitFor();
    expect(await create.isEnabled()).toBe(true);
    await view.click("text=Create Subscription");

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/topics/${topicId}/subscriptions`);
    expect(sent.body.order_index).toBe(0);
    expect(sent.body.match_rules).toEqual({ event_type: "order.created" });
    expect(sent.body.mapping).toEqual({
      engine: "jsonata",
      version: "1",
      expression: '{\n  "order": orderId\n}',
    });
    expect(sent.body.http_delivery).toMatchObject({ version: 1, method: "POST", body: "json", headers: {} });
    await view.close();
  }, 60_000);

  it("sends a Source, then opens its setup guide without narrow-screen overflow", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic"), /Orders/);
    await choose(form.getByLabel("Type"), "Event API");
    await form.getByLabel("Configuration (JSON)").fill("{}");
    await view.click("text=Create Source");

    const guide = view.getByRole("dialog", { name: "Publish through this Source" });
    await guide.getByRole("heading", { name: "Construct the Event request" }).waitFor();
    await view.setViewportSize({ width: 320, height: 900 });
    expect(await view.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await view.keyboard.press("Escape");
    const reopen = view.getByRole("button", { name: "Open setup guide" });
    expect(await reopen.evaluate((button) => document.activeElement === button)).toBe(true);

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/sources`);
    expect(sent.body.connector_id).toBe(connectorId);
    expect(sent.body.topic_id).toBe(topicId);
    expect(sent.body.type).toBe("event_api");
    expect(sent.body.configuration).toEqual({});
    await view.close();
  }, 60_000);

  /// Webhook and queue Sources carry the parts an Event API Source refuses: verification (webhook
  /// only), input requirements, a mapping envelope, and an Event identity rule. Each kind is typed
  /// as its label tells the Operator to type it, so a label naming a kind the API does not accept
  /// fails here rather than at the first real create.
  it("sends a webhook Source with verification, contract, mapping, and identity rule", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic"), /Orders/);
    await choose(form.getByLabel("Type"), "Webhook");
    await form.getByLabel("Configuration (JSON)", { exact: true }).fill("{}");
    await form.getByLabel("Verification scheme (optional)").fill("hmac_sha256");
    await form.getByLabel("Verification configuration (JSON)").fill('{"header":"X-Signature"}');
    await form.getByLabel("Verification secret references (JSON)").fill('{"secret":"gh-hook"}');
    await form.getByLabel("Input requirements (JSON, optional)").fill('{"type":"object"}');
    await form.getByLabel("Event mapping (JSONata, optional)").fill("payload");
    await form.getByLabel(/^Event identity kind \(header or json_path\)$/).fill("json_path");
    await form.getByLabel("Event identity selector").fill("/delivery/id");
    await view.click("text=Create Source");

    const sent = await submitted(writes);
    expect(sent.body.type).toBe("webhook");
    expect(sent.body.verification).toEqual({
      scheme: "hmac_sha256",
      config: { header: "X-Signature" },
      secret_refs: { secret: "gh-hook" },
    });
    expect(sent.body.input_requirements).toEqual({ type: "object" });
    expect(sent.body.mapping).toEqual({ engine: "jsonata", version: "1", expression: "payload" });
    expect(sent.body.event_identity_rule).toEqual({ kind: "json_path", value: "/delivery/id" });
    await view.close();
  }, 60_000);

  it("sends a queue Source with its transport configuration and no verification", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic"), /Orders/);
    await choose(form.getByLabel("Type"), "Queue");
    expect(await form.getByLabel(/^Verification/).count()).toBe(0);
    await form.getByLabel("Queue transport configuration (JSON)").fill('{"transport":"azure_service_bus"}');
    await form.getByLabel(/^Event identity kind \(message_id or json_path\)$/).fill("message_id");
    await form.getByLabel("Event identity selector").fill("message_id");
    await view.click("text=Create Source");

    const sent = await submitted(writes);
    expect(sent.body.type).toBe("queue");
    expect(sent.body.configuration).toEqual({ transport: "azure_service_bus" });
    expect(sent.body.verification).toBeNull();
    expect(sent.body.event_identity_rule).toEqual({ kind: "message_id", value: "message_id" });
    await view.close();
  }, 60_000);
});

describe("Update and deactivate, driven through a real browser", () => {
  // Tenant, Topic and Source updates send only plain strings and are covered by the jsdom suite.
  // What is exercised here is what a string-only form has to convert: parsed configuration
  // documents, plus the two shapes with no other coverage at all — a confirmed deactivate and the
  // one DELETE the dashboard issues.

  it("sends an updated Destination with its config reparsed and authentication untouched", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/destinations/${destinationId}`);

    // Editing is a deliberate act now rather than the panel's resting state, and the form names
    // itself instead of repeating on screen the heading its disclosure already carries.
    await view.getByRole("button", { name: "Edit", exact: true }).click();
    await formNamed(view, "Edit sink")
      .getByLabel("Configuration (JSON)", { exact: true })
      .fill('{"base_uri":"http://moved.invalid"}');
    await view.click("text=Save changes");

    const sent = await submitted(writes);
    expect(sent.method).toBe("PUT");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/destinations/${destinationId}`);
    expect(sent.body.configuration).toEqual({ base_uri: "http://moved.invalid" });
    expect(sent.body.authentication).toBeNull();
    await view.close();
  }, 60_000);

  it("preserves a Subscription's hidden order while clearing its mapping", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`);

    const mapping = view.getByRole("heading", { name: "Mapping" }).locator("..");
    expect(await mapping.locator("dl").textContent()).toContain("order←orderId");
    expect(await mapping.locator("dl").textContent()).toContain("placed_at←placedAt");
    await mapping.getByText(/Evaluated per Event before delivery/).waitFor();
    expect(await view.getByText("Order", { exact: true }).count()).toBe(0);
    // Editing opens the same sheet creating does, so the form is reached by opening it.
    await view.getByRole("button", { name: "Edit", exact: true }).click();
    const form = formNamed(view, "Edit to-sink");
    expect(await form.locator('section[aria-labelledby="mapping-summary-heading"] dl').textContent()).toContain(
      "placed_at←placedAt",
    );
    expect(await form.getByLabel("Order").count()).toBe(0);
    const save = view.locator('form[aria-label="Edit to-sink"] button[type="submit"]');
    await form.getByRole("button", { name: "Edit in Playground" }).click();
    const playground = view.getByRole("dialog", { name: "Mapping Playground" });
    await playground.getByRole("button", { name: "Remove mapping 3" }).click();
    await playground.getByRole("button", { name: "Remove mapping 2" }).click();
    await playground.getByRole("button", { name: "Remove mapping 1" }).click();
    await playground.getByText("No fields mapped. The accepted payload will be delivered unchanged.").waitFor();
    expect(await save.isDisabled()).toBe(true);
    await playground.getByRole("button", { name: "Preview mapping" }).click();
    await playground
      .getByRole("heading", { name: "Preview body" })
      .locator("..")
      .getByText('"orderId": "SO-4014"')
      .waitFor();
    await playground.getByRole("button", { name: "Confirm mapping change" }).click();
    await view.click("text=Save changes");

    const sent = await submitted(writes);
    expect(sent.method).toBe("PUT");
    expect(sent.body.order_index).toBe(1);
    expect(sent.body.mapping).toBeNull();
    expect(sent.body.match_rules).toEqual({ event_type: "order.created" });
    await view.close();
  }, 60_000);

  it("requires a fresh preview and confirmation before saving a changed mapping", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`);

    await view.getByRole("button", { name: "Playground", exact: true }).click();
    const playground = view.getByRole("dialog", { name: "Mapping Playground" });
    await playground.waitFor();
    expect(await playground.getByLabel("Output field 1").inputValue()).toBe("order");
    expect(await playground.getByLabel("Event field 1").inputValue()).toBe("orderId");
    expect(await playground.getByLabel("Event field 1").locator('option[value="customer.id"]').count()).toBe(1);
    expect(await playground.getByLabel("Event field 1").locator('option[value="`placed-at`"]').count()).toBe(1);
    await playground.getByRole("button", { name: "Advanced JSONata" }).click();
    expect(await playground.getByLabel("Playground mapping expression").inputValue()).toBe(
      '{ "order": orderId, "total": total, "placed_at": placedAt }',
    );
    await playground.getByRole("button", { name: "Field mapping" }).click();
    await playground.getByRole("button", { name: "Add field" }).click();
    await playground.getByLabel("Output field 4").fill("type");
    await playground.getByLabel("Event field 4").selectOption("$context.event_type");
    await playground.getByText("Back to Subscription", { exact: true }).click();
    const form = formNamed(view, "Edit to-sink");
    await form.getByText("Preview and confirm this mapping change before saving the Subscription.").waitFor();
    expect(await form.getByRole("button", { name: "Save changes" }).isDisabled()).toBe(true);

    await form.getByRole("button", { name: "Edit in Playground" }).click();
    await playground.getByRole("button", { name: "Preview mapping" }).click();
    await playground.getByText('"order": "SO-4014"').waitFor();

    const preview = writes.find((request) => new URL(request.url()).pathname === "/admin/transform/preview");
    expect(preview?.postDataJSON()).toMatchObject({
      transform: {
        engine: "jsonata",
        version: "1",
        expression:
          '{\n  "order": orderId,\n  "total": total,\n  "placed_at": placedAt,\n  "type": $context.event_type\n}',
      },
      sample_input: { orderId: "SO-4014", total: 42 },
      sample_context: {
        event_type: "order.created",
        topic_name: "orders",
        accepted_at: "2026-09-08T12:00:00Z",
      },
    });

    await playground.getByRole("button", { name: "Paste sample JSON" }).click();
    expect(await playground.getByRole("button", { name: "Confirm mapping change" }).isDisabled()).toBe(true);
    await playground.getByLabel("Sample input (JSON)").fill('{"orderId":"manual-1"}');
    await playground.getByRole("button", { name: "Preview mapping" }).click();
    await playground.getByText('"order": "SO-4014"').waitFor();
    await view.setViewportSize({ width: 320, height: 900 });
    expect(await view.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await playground.getByRole("button", { name: "Confirm mapping change" }).click();
    await form.getByText("Mapping change reviewed and ready to save.").waitFor();
    expect(await form.locator('section[aria-labelledby="mapping-summary-heading"] dl').textContent()).toContain(
      "type←$context.event_type",
    );
    expect(await form.getByRole("button", { name: "Save changes" }).isEnabled()).toBe(true);
    await view.close();
  }, 60_000);

  it("opens a Source guide from a Subscription with mapping context", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`);
    await view.route(`**/subscriptions/${subscriptionId}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: "application/json",
        json: { ...subscriptionDetail, match_rules: { event_type: "order's.placed" } },
      }),
    );
    await view.reload();

    await view.getByRole("link", { name: new RegExp(sourceId) }).click();
    await view.getByRole("heading", { name: "Publish through this Source" }).waitFor();
    await view.getByText('"event_type": "order\'s.placed"').first().waitFor();
    await view.getByText('"orderId": null').first().waitFor();
    expect(
      await view.getByRole("heading", { name: "cURL request" }).locator("../..").locator("pre").textContent(),
    ).toContain(`order'"'"'s.placed`);

    await view.close();

    const advanced = await open(`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`);
    await advanced.page.route(`**/subscriptions/${subscriptionId}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: "application/json",
        json: {
          ...subscriptionDetail,
          mapping_config: { engine: "jsonata", version: "1", expression: "$merge(payload)" },
        },
      }),
    );
    await advanced.page.reload();
    await advanced.page.getByRole("link", { name: new RegExp(sourceId) }).click();
    await advanced.page.getByRole("link", { name: "Open its Mapping Playground" }).click();
    await advanced.page.getByRole("dialog", { name: "Mapping Playground" }).waitFor();
    await advanced.page.close();
  }, 60_000);

  it("separates mapping syntax from manual-sample evaluation failure and restores focus", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`);
    const previews: Record<string, unknown>[] = [];
    await view.route("**/admin/transform/preview", async (route) => {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      previews.push(body);
      const expression = (body.transform as { expression: string }).expression;
      return route.fulfill({
        status: 400,
        contentType: "application/problem+json",
        json: {
          title: "One or more validation errors occurred.",
          errors: expression === "[" ? { transform: ["Invalid JSONata expression."] } : { "": ["items required"] },
        },
      });
    });

    await view.getByRole("button", { name: "Playground", exact: true }).click();
    const playground = view.getByRole("dialog", { name: "Mapping Playground" });
    await playground.getByRole("button", { name: "Advanced JSONata" }).click();
    const expression = playground.getByLabel("Playground mapping expression");
    await expression.fill("[");
    expect(await playground.getByRole("button", { name: "Field mapping" }).isDisabled()).toBe(true);
    await playground.getByRole("button", { name: "Preview mapping" }).click();
    await playground.getByText("Invalid JSONata expression.").waitFor();
    expect(await playground.getByText(/would retry and may dead-letter/).count()).toBe(0);
    expect(await playground.getByRole("button", { name: "Confirm mapping change" }).isDisabled()).toBe(true);

    await expression.fill('$error("items required")');
    await playground.getByRole("button", { name: "Paste sample JSON" }).click();
    await playground.getByLabel("Sample input (JSON)").fill('{"orderId":"manual-1"}');
    await playground.getByLabel("Event type").fill("order.manual");
    await playground.getByLabel("Accepted at").fill("2026-09-08T10:30");
    await playground.getByRole("button", { name: "Preview mapping" }).click();
    await playground.getByText(/would retry and may dead-letter/).waitFor();
    expect(await playground.getByRole("button", { name: "Confirm mapping change" }).isDisabled()).toBe(true);
    expect(previews.at(-1)).toMatchObject({
      sample_input: { orderId: "manual-1" },
      sample_context: { event_type: "order.manual", topic_name: "orders" },
    });

    await view.keyboard.press("Escape");
    const reopen = formNamed(view, "Edit to-sink").getByRole("button", { name: "Edit in Playground" });
    expect(await reopen.evaluate((button) => document.activeElement === button)).toBe(true);
    await reopen.click();
    await playground.getByRole("heading", { name: "Advanced JSONata" }).waitFor();
    expect(await playground.getByLabel("Playground mapping expression").inputValue()).toBe('$error("items required")');
    await view.keyboard.press("Escape");
    await expect.poll(() => reopen.evaluate((button) => document.activeElement === button)).toBe(true);
    await view.keyboard.press("Escape");
    const trigger = view.getByRole("button", { name: "Playground", exact: true });
    expect(await trigger.evaluate((button) => document.activeElement === button)).toBe(true);
    await view.close();
  }, 60_000);

  it("deactivates a Topic only after the confirmation naming it", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/topics/${topicId}`);

    await view.getByRole("button", { name: "Deactivate", exact: true }).click();
    await view.getByText(/Deactivate the Topic "Orders"\?/).waitFor();
    // Arming the confirmation must not be the action itself.
    expect(writes, "Deactivation ran before it was confirmed.").toHaveLength(0);

    await view.getByRole("button", { name: "Cancel" }).click();
    const trigger = view.getByRole("button", { name: "Deactivate" });
    expect(await trigger.evaluate((button) => document.activeElement === button)).toBe(true);

    await trigger.click();
    await view.getByRole("button", { name: "Deactivate Orders" }).click();

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/topics/${topicId}/deactivate`);
    await view.close();
  }, 60_000);

  it("revokes a Source with the one DELETE the dashboard issues", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources/${sourceId}`);

    await view.getByRole("button", { name: "Revoke", exact: true }).click();
    expect(writes, "Revocation ran before it was confirmed.").toHaveLength(0);
    await view.click(`text=Revoke ${sourceId}`);

    const sent = await submitted(writes);
    expect(sent.method).toBe("DELETE");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/sources/${sourceId}`);
    await view.close();
  }, 60_000);
});
