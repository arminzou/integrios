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
  description: null,
  subscription_count: 1,
  event_types: ["order.created", "order.refunded"],
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
  description: null,
  ...stamps,
};
/// A real manifest always carries its capability blocks, and the Source form now authors its
/// verification menu from them, so an empty document here would prove the form renders nothing.
const sourceVerification = {
  allow_unverified: true,
  schemes: [{ scheme: "hmac_sha256", required_config: [], required_secret_refs: ["secret"] }],
};
const connectorDetail = {
  ...connector,
  manifest_schema_version: 1,
  manifest: {
    manifest_schema_version: 1,
    key: connector.key,
    contract_version: 1,
    direction: connector.direction,
    source_configuration_schema: { type: "object", properties: {}, additionalProperties: true },
    destination_configuration_schema: {
      type: "object",
      properties: { base_uri: { type: "string", format: "uri" } },
      required: ["base_uri"],
      additionalProperties: false,
    },
    source_verification: sourceVerification,
    destination_authentication: {
      allow_unauthenticated: true,
      schemes: [
        { scheme: "api_key_header", required_config: ["header_name"], required_secret_refs: ["api_key"] },
        { scheme: "bearer_token", required_config: [], required_secret_refs: ["token"] },
      ],
    },
    presentation: { name: connector.name, event_types: [], authoring_presets: [] },
  },
};

const page = (items: unknown[], nextCursor: string | null = null) => ({ items, next_cursor: nextCursor });

const subscriptionId = "77777777-7777-7777-7777-777777777777";
const sourceId = "88888888-8888-8888-8888-888888888888";
const eventId = "99999999-9999-9999-9999-999999999999";

const destinationDetail = {
  ...destination,
  configuration: { base_uri: "http://sink.invalid" },
  authentication: null,
};
const subscriptionDetail = {
  id: subscriptionId,
  topic_id: topicId,
  tenant_id: tenantId,
  name: "to-sink",
  event_types: ["order.created"],
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
  name: "orders-intake",
  type: "event_api",
  event_types: ["order.created"],
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
    await view.getByText("Event API", { exact: true }).waitFor();
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
  ["Import manifest", "/connectors", "Import manifest"],
  ["Subscription edit", `/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`, "Edit"],
  ["New Source", `/tenants/${tenantId}/sources`, "New Source"],
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

it("keeps a valid Connector draft visible and unappliable while Admin composes it", async () => {
  const { page: view } = await open("/connectors");
  let finish!: () => void;
  const held = new Promise<void>((resolve) => {
    finish = resolve;
  });
  await view.route("**/admin/connectors/*/versions/*/compose", async (route) => {
    await held;
    await route.fulfill({ json: { manifest: { composed_by: "admin" } } });
  });

  try {
    await view.getByRole("button", { name: "New Connector" }).click();
    const form = view.getByRole("dialog", { name: "New Connector" }).locator("form");
    await form.getByLabel("Name", { exact: true }).fill("GitHub");
    await form.getByLabel("Key", { exact: true }).fill("github");

    await form.getByRole("status").getByText("Composing manifest…").waitFor();
    expect(await form.getByRole("button", { name: "Create Connector" }).isDisabled()).toBe(true);
    expect(await form.getByLabel("Name", { exact: true }).inputValue()).toBe("GitHub");
  } finally {
    finish();
    await view.close();
  }
}, 60_000);

it("keeps a Connector draft visible and unappliable when Admin cannot be reached", async () => {
  const { page: view } = await open("/connectors");
  await view.route("**/admin/connectors/*/versions/*/compose", (route) => route.abort("connectionrefused"));

  try {
    await view.getByRole("button", { name: "New Connector" }).click();
    const form = view.getByRole("dialog", { name: "New Connector" }).locator("form");
    await form.getByLabel("Name", { exact: true }).fill("GitHub");
    await form.getByLabel("Key", { exact: true }).fill("github");

    await form.getByText("The Admin API could not be reached.").waitFor();
    expect(await form.getByRole("button", { name: "Create Connector" }).isDisabled()).toBe(true);
    expect(await form.getByLabel("Name", { exact: true }).inputValue()).toBe("GitHub");
    expect(await form.getByLabel("Key", { exact: true }).inputValue()).toBe("github");
  } finally {
    await view.close();
  }
}, 60_000);

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
  // Dry runs are POSTs that change nothing: the Mapping Playground's preview, and the acceptance
  // check the Event Builder runs on its own while an Operator edits.
  const dryRuns = ["/admin/transform/preview", "/admin/connectors/source-contracts/preview"];
  const request = writes.find((candidate) => !dryRuns.includes(new URL(candidate.url()).pathname));
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
    await form.getByLabel("Name", { exact: true }).fill("sink");
    await form.getByLabel("Base URI").fill("http://sink.invalid");
    await view.click("text=Create Destination");

    // The field-keyed message lands on the control it names, whatever casing the server used.
    const name = form.getByLabel("Name");
    await expect.poll(() => name.getAttribute("aria-invalid")).toBe("true");
    await view.getByText("That name is already taken.").waitFor();
    // What is attributed to no rendered field is still said, at the level of the form.
    await view.getByText("The deployment refused the write.").waitFor();
    await view.close();
  }, 60_000);

  it("sends a Destination from the Connector's guided fields", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/destinations`);

    // The create form sits behind a "New Destination" disclosure so it does not permanently
    // dominate the list above it.
    await view.click("text=New Destination");
    const form = formNamed(view, "Create a Destination");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await form.getByLabel("Name").fill("sink");
    await form.getByLabel("Base URI").fill("http://sink.invalid");
    await view.click("text=Create Destination");
    await view.waitForFunction(() => true);

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/destinations`);
    expect(writes[0].headers()[session.antiforgery_header_name.toLowerCase()]).toBe(session.antiforgery_token);
    expect(sent.body.connector_id).toBe(connectorId);
    expect(sent.body.configuration).toEqual({ base_uri: "http://sink.invalid" });
    expect(sent.body.authentication).toBeNull();
    await view.close();
  }, 60_000);

  it("authors every constrained Destination scalar type and declared authentication", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/destinations`);
    const sourceOnly = {
      ...connector,
      id: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      name: "Source only",
      direction: "source",
    };
    const disabled = { ...connector, id: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", name: "Removed", direction: "source" };
    await view.route("**/admin/connectors?*", (route) =>
      route.fulfill({ json: page([connector, sourceOnly, disabled]) }),
    );
    await view.route(`**/admin/connectors/${connectorId}`, (route) =>
      route.fulfill({
        json: {
          ...connectorDetail,
          manifest: {
            ...connectorDetail.manifest,
            destination_configuration_schema: {
              type: "object",
              properties: {
                base_uri: { type: "string", format: "uri", minLength: 8, maxLength: 100 },
                host_name: { type: "string", format: "hostname" },
                mode: { type: "string", enum: ["fast", "safe"] },
                retry_ratio: { type: "number", minimum: 0, maximum: 1 },
                attempts: { type: "integer", minimum: 1, maximum: 5 },
                enabled: { type: "boolean" },
              },
              required: ["base_uri", "host_name", "mode", "retry_ratio", "attempts", "enabled"],
              additionalProperties: false,
            },
          },
        },
      }),
    );
    await view.reload();

    await view.click("text=New Destination");
    const form = formNamed(view, "Create a Destination");
    await form.getByLabel("Connector").click();
    expect(await view.getByRole("option").allInnerTexts()).toEqual(["HTTP (v1)"]);
    await view.getByRole("option", { name: "HTTP (v1)" }).click();
    await form.getByLabel("Base URI").fill("https://sink.invalid");
    await form.getByLabel("Host name").fill("sink.invalid");
    await choose(form.getByLabel("Mode"), "Fast");
    await form.getByLabel("Retry ratio").fill("0.5");
    await form.getByLabel("Attempts").fill("3");
    await choose(form.getByLabel("Enabled"), "True");
    await choose(form.getByRole("combobox", { name: "Authentication" }), "API key header");
    await form.getByLabel("Header name").fill("X-Api-Key");
    await form.getByLabel("API key secret reference").fill("northwind-api-key");
    await form.getByLabel("Name", { exact: true }).fill("sink");
    await view.click("text=Create Destination");

    const sent = await submitted(writes);
    expect(sent.body.configuration).toEqual({
      base_uri: "https://sink.invalid",
      host_name: "sink.invalid",
      mode: "fast",
      retry_ratio: 0.5,
      attempts: 3,
      enabled: true,
    });
    expect(sent.body.authentication).toEqual({
      scheme: "api_key_header",
      config: { header_name: "X-Api-Key" },
      secret_refs: { api_key: "northwind-api-key" },
    });
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
    expect(
      await view.getByRole("dialog", { name: "New Subscription" }).getByLabel("Topic", { exact: true }).textContent(),
    ).toContain("Orders");
    const form = formNamed(view, "Create a Subscription");
    await form.getByLabel("Name").fill("to-sink");
    await choose(form.getByLabel("Destination"), /sink/);
    await form.getByRole("checkbox", { name: "order.created" }).check();
    await choose(form.getByLabel("Method"), "PATCH");
    await form.getByLabel("Relative path (optional)").fill("orders?notify=true");
    // A request without a body has nothing to map, so the mapping is not offered for one.
    await choose(form.getByLabel("Request body"), "No body");
    expect(await form.getByRole("button", { name: "Add mapping in Playground" }).count()).toBe(0);
    await choose(form.getByLabel("Request body"), "Mapped Event JSON");
    await form.getByRole("button", { name: "Add header" }).click();
    await form.getByLabel("Header 1 name").fill("X-Workflow");
    await form.getByLabel("Header 1 value").fill("priority");
    await choose(form.getByLabel("Success check"), "Response JSON boolean");
    await form.getByLabel("Boolean field").fill("ok");
    await choose(form.getByLabel("Expected value"), "True");
    await form.getByLabel("Diagnostic field (optional)").fill("error");
    await form.getByLabel("Maximum response bytes (optional)").fill("4096");
    expect(await form.getByLabel("Order", { exact: true }).count()).toBe(0);
    expect(await form.getByLabel("Match rules (JSON)").count()).toBe(0);
    expect(await form.getByLabel("Mapping expression (optional)").count()).toBe(0);
    expect(await form.getByLabel("Raw mapping (JSON)").count()).toBe(0);
    const create = view.locator('form[aria-label="Create a Subscription"] button[type="submit"]');
    // Samples are the Subscription's own Event type: another type's payload is a shape it never maps.
    const samplesRead = view.waitForRequest((request) => new URL(request.url()).pathname.endsWith("/events"));
    await form.getByRole("button", { name: "Add mapping in Playground" }).click();
    const playground = view.getByRole("dialog", { name: "Mapping Playground" });
    expect(new URL((await samplesRead).url()).searchParams.get("event_type")).toBe("order.created");
    await playground.getByText("1 of 1").waitFor();
    expect(await playground.getByRole("button", { name: "Newer Event" }).isDisabled()).toBe(true);
    expect(await playground.getByRole("button", { name: "Older Event" }).isDisabled()).toBe(true);
    await playground.getByLabel("Output field 1").fill("order");
    await playground.getByLabel("Event field 1").selectOption("orderId");
    expect(await create.isDisabled()).toBe(true);
    await playground.getByRole("button", { name: "Preview mapping" }).click();
    await playground.getByText('"order": "SO-4014"').waitFor();
    await playground.getByRole("button", { name: "Confirm mapping change" }).click();
    await form.getByText("Mapping change reviewed and ready to save.").waitFor();
    expect(await create.isEnabled()).toBe(true);
    await view.setViewportSize({ width: 320, height: 900 });
    expect(await view.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await view.click("text=Create Subscription");

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/topics/${topicId}/subscriptions`);
    expect(sent.body.order_index).toBe(0);
    expect(sent.body.event_types).toEqual(["order.created"]);
    expect(sent.body.mapping).toEqual({
      engine: "jsonata",
      version: "1",
      expression: '{\n  "order": orderId\n}',
    });
    expect(sent.body.http_delivery).toEqual({
      version: 1,
      method: "PATCH",
      path: "orders?notify=true",
      body: "json",
      headers: { "X-Workflow": "priority" },
    });
    expect(sent.body.http_success).toEqual({
      evaluator: "json_boolean",
      field: "ok",
      expected: true,
      diagnostic_field: "error",
      max_body_bytes: 4096,
    });
    await view.close();
  }, 60_000);

  it("steps a previewed mapping through the Subscription's own Events", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/subscriptions?topic_id=${topicId}`);
    const samples = ["SO-1", "SO-2"].map((order, index) => ({
      event_id: `0000000${index + 1}-0000-0000-0000-000000000000`,
      topic_id: topicId,
      source_event_id: `created-${order}`,
      event_type: "order.created",
      status: "unrouted",
      accepted_at: `2026-09-08T12:0${2 - index}:00Z`,
      deliveries: { pending: 0, in_flight: 0, succeeded: 0, dead_lettered: 0 },
      // Only the newest sample carries an email: the shape difference stepping exists to reveal.
      payload: index === 0 ? { orderId: order, email: "buyer@example.test" } : { orderId: order },
    }));
    await view.route(
      (url) => url.pathname.endsWith("/events"),
      (route) => route.fulfill({ json: { items: samples, next_cursor: null } }),
    );
    await view.route(
      (url) => url.pathname.endsWith("/deliveries"),
      (route) => {
        const id = new URL(route.request().url()).pathname.split("/").at(-2);
        const sample = samples.find((item) => item.event_id === id);
        return route.fulfill({ json: { ...sample, tenant_id: tenantId, event_deliveries: [], delivery_attempts: [] } });
      },
    );
    // The preview echoes the sample it was sent, so each step's result names the Event it ran on.
    await view.route("**/admin/transform/preview", (route) =>
      route.fulfill({ json: { output: { email: route.request().postDataJSON().sample_input.email } } }),
    );

    await view.click("text=New Subscription");
    const form = formNamed(view, "Create a Subscription");
    await form.getByRole("checkbox", { name: "order.created" }).check();
    await form.getByRole("button", { name: "Add mapping in Playground" }).click();
    const playground = view.getByRole("dialog", { name: "Mapping Playground" });
    await playground.getByText("1 of 2").waitFor();
    await playground.getByLabel("Output field 1").fill("email");
    await playground.getByLabel("Event field 1").selectOption("email");
    const previewBody = playground.locator("section", { has: view.getByRole("heading", { name: "Preview body" }) });
    await playground.getByRole("button", { name: "Preview mapping" }).click();
    await previewBody.getByText('"email": "buyer@example.test"').waitFor();

    // Stepping re-runs the preview the Operator already asked for, against the next sample.
    await playground.getByRole("button", { name: "Older Event" }).click();
    await playground.getByText("2 of 2").waitFor();
    await previewBody.getByText("{}", { exact: true }).waitFor({ timeout: 5_000 });
    expect(await playground.getByRole("button", { name: "Older Event" }).isDisabled()).toBe(true);
    await view.close();
  }, 60_000);

  it("creates a Subscription for an unrouted Event's type from its inspector", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/events/${eventId}`);
    // The shared handler answers Events as routed; this one is the unrouted case the action is for.
    await view.route(
      (url) => url.pathname.endsWith(`/events/${eventId}/deliveries`),
      (route) =>
        route.fulfill({
          json: {
            event_id: eventId,
            topic_id: topicId,
            event_type: "order.refunded",
            status: "unrouted",
            accepted_at: "2026-09-08T12:00:00Z",
            payload: { orderId: "SO-4014" },
            event_deliveries: [],
            delivery_attempts: [],
          },
        }),
    );
    await view.reload();

    const inspector = view.getByRole("complementary", { name: "Event detail" });
    await inspector.getByRole("link", { name: "Create Subscription" }).click();

    const sheet = view.getByRole("dialog", { name: "New Subscription" });
    expect(await sheet.getByLabel("Topic", { exact: true }).textContent()).toContain("Orders");
    expect(
      await formNamed(view, "Create a Subscription").getByRole("checkbox", { name: "order.refunded" }).isChecked(),
    ).toBe(true);
    await view.close();
  }, 60_000);

  it("sends a Source, then opens its setup guide without narrow-screen overflow", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic"), /Orders/);
    await choose(form.getByLabel("Type"), "Event API");
    await form.getByLabel("Name", { exact: true }).fill("orders-intake");
    expect(await form.getByText("Advanced configuration").count()).toBe(0);
    await form.getByLabel("Event type 1", { exact: true }).fill("order.created");
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
    expect(sent.body.event_types).toEqual(["order.created"]);
    await view.close();
  }, 60_000);

  /// Webhook and broker Sources carry the parts an Event API Source refuses. Their authored choices
  /// are converted to the API's existing documents at the form boundary.
  it("sends a webhook Source with verification and an identity rule", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic"), /Orders/);
    await choose(form.getByLabel("Type"), "Webhook");
    await form.getByLabel("Name", { exact: true }).fill("github-intake");
    await choose(form.getByRole("combobox", { name: "Verification" }), "HMAC SHA-256");
    await form.getByLabel("Secret reference").fill("gh-hook");
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    const builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    // The rule reads a request header, so the sample has to carry the header it reads.
    await builder.getByLabel("Header 1 name").fill("X-GitHub-Delivery");
    await builder.getByLabel("Header 1 sample value").fill("d-1");
    await builder.getByLabel("Event identity").selectOption("header");
    await builder.getByLabel("Identity header").selectOption("x-github-delivery");
    await expect.poll(() => builder.getByRole("button", { name: "Use configuration" }).isEnabled()).toBe(true);
    await builder.getByRole("button", { name: "Use configuration" }).click();
    await form.getByText("x-github-delivery", { exact: true }).waitFor();
    await form.getByLabel("Event type 1", { exact: true }).fill("order.created");
    await view.click("text=Create Source");

    const sent = await submitted(writes);
    expect(sent.body.type).toBe("webhook");
    expect(sent.body.configuration).toEqual({});
    expect(sent.body.verification).toEqual({
      scheme: "hmac_sha256",
      config: {},
      secret_refs: { secret: "gh-hook" },
    });
    expect(sent.body.input_requirements).toBeNull();
    expect(sent.body.mapping).toBeNull();
    // allow_missing defaults to refusing, so a rule authored without touching it keeps today's meaning.
    // Lower-cased, as the runtime presents request headers; the extractor matches case-insensitively.
    expect(sent.body.event_identity_rule).toEqual({
      kind: "header",
      value: "x-github-delivery",
      allow_missing: false,
    });
    await view.close();
  }, 60_000);

  /// The one asymmetry the Event Builder used to own: an identity that may be absent without the
  /// request being refused. The rule carries it now, so the Builder no longer offers an identity.
  it("sends an identity rule that permits a missing value when that is chosen", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic", { exact: true }), /Orders/);
    await choose(form.getByLabel("Type"), "Webhook");
    await form.getByLabel("Name", { exact: true }).fill("github-intake");
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    const builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    await builder.getByLabel("Request body (JSON)").fill('{"delivery":{"id":"d-1"}}');
    await builder.getByLabel("Event identity").selectOption("json_path");
    const field = builder.getByLabel("Identity field");
    // Picked as the field the sample shows, stored as the JSON Pointer the extractor reads.
    await expect.poll(() => field.locator("option").last().textContent()).toBe("delivery.id");
    await field.selectOption("/delivery/id");
    await builder.getByLabel("Accept a request that carries no value here").check();
    await expect.poll(() => builder.getByRole("button", { name: "Use configuration" }).isEnabled()).toBe(true);
    await builder.getByRole("button", { name: "Use configuration" }).click();
    await form.getByLabel("Event type 1", { exact: true }).fill("order.created");
    await view.click("text=Create Source");

    const sent = await submitted(writes);
    expect(sent.body.event_identity_rule).toEqual({
      kind: "json_path",
      value: "/delivery/id",
      allow_missing: true,
    });
    await view.close();
  }, 60_000);

  it("offers the missing-value permission for a message id, which a publisher may never set", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic", { exact: true }), /Orders/);
    await choose(form.getByLabel("Type"), "Message broker");
    await form.getByLabel("Name", { exact: true }).fill("broker-intake");
    await form.getByLabel("Namespace").fill("acme.servicebus.windows.net");
    await form.getByLabel("Queue name").fill("orders");
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    const builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    await builder.getByLabel("Event identity").selectOption("message_id");
    // No selector for this kind — the message carries its own id — but the permission still applies.
    expect(await builder.getByLabel("Identity field").count()).toBe(0);
    await builder.getByLabel("Accept a message that carries no value here").check();
    await builder.getByRole("button", { name: "Use configuration" }).click();
    await form.getByLabel("Event type 1", { exact: true }).fill("order.created");
    await view.click("text=Create Source");

    const sent = await submitted(writes);
    expect(sent.body.event_identity_rule).toEqual({
      kind: "message_id",
      value: "message_id",
      allow_missing: true,
    });
    await view.close();
  }, 60_000);

  /// The Event-identity rule is fixed when the Source is created, so a selector left over from the
  /// kind before it is permanent. Switching kind must clear it rather than offer a header name in a
  /// field that now wants a JSON Pointer — which the API accepts as a header name in the other
  /// direction, producing a Source that rejects every request it ever receives.
  it("clears the identity selector when the identity kind changes, and can return to none", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic", { exact: true }), /Orders/);
    await choose(form.getByLabel("Type"), "Webhook");
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    const builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    await builder.getByLabel("Header 1 name").fill("X-GitHub-Delivery");
    await builder.getByLabel("Header 1 sample value").fill("d-1");
    await builder.getByLabel("Event identity").selectOption("header");
    await builder.getByLabel("Identity header").selectOption("x-github-delivery");

    await builder.getByLabel("Event identity").selectOption("json_path");
    expect(await builder.getByLabel("Identity field").inputValue()).toBe("");
    // Half a rule cannot leave the dialog: it would be stored and match nothing.
    expect(await builder.getByRole("button", { name: "Use configuration" }).isEnabled()).toBe(false);

    await builder.getByLabel("Event identity").selectOption("");
    expect(await builder.getByLabel("Identity field").count()).toBe(0);
    expect(await builder.getByLabel("Accept a request that carries no value here").count()).toBe(0);
    await view.close();
  }, 60_000);

  /// The other broker entity form and the other authentication scheme. Both compose a different
  /// configuration document, and the API accepts exactly one entity, so a form that named a queue
  /// and a topic subscription together would be refused.
  it("sends a topic subscription addressed with a connection string reference", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic", { exact: true }), /Orders/);
    await choose(form.getByLabel("Type"), "Message broker");
    await form.getByLabel("Name", { exact: true }).fill("orders-topic");
    await form.getByLabel("Namespace").fill("acme.servicebus.windows.net");
    await choose(form.getByLabel("Broker entity"), "Topic subscription");
    await form.getByLabel("Topic name").fill("orders");
    await form.getByLabel("Subscription name").fill("integrios");
    await choose(form.getByLabel("Authentication"), "Connection string reference");
    await form.getByLabel("Connection string reference", { exact: true }).fill("orders-bus");
    await form.getByLabel("Event type 1", { exact: true }).fill("order.created");
    await view.click("text=Create Source");

    const sent = await submitted(writes);
    expect(sent.body.configuration).toEqual({
      transport: "azure_service_bus",
      authentication: { scheme: "connection_string", secret_ref: "orders-bus" },
      transport_config: {
        namespace: "acme.servicebus.windows.net",
        topic_name: "orders",
        subscription_name: "integrios",
      },
    });
    await view.close();
  }, 60_000);

  /// The verification menu is the Connector's, not the dashboard's. A Connector that refuses an
  /// unverified Source must not offer the empty choice, and a scheme the Connector does not declare
  /// must not appear at all.
  it("offers the verification the Connector declares and no empty choice when it requires one", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/sources`);
    await view.route("**/admin/connectors/*", (route) =>
      route.fulfill({
        json: {
          ...connectorDetail,
          manifest: {
            ...connectorDetail.manifest,
            source_verification: { ...sourceVerification, allow_unverified: false },
          },
        },
      }),
    );

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Type"), "Webhook");

    await form.getByRole("combobox", { name: "Verification" }).click();
    const options = view.getByRole("listbox");
    await options.getByRole("option", { name: "HMAC SHA-256" }).waitFor();
    expect(await options.getByRole("option").allInnerTexts()).toEqual(["HMAC SHA-256"]);
    await view.close();
  }, 60_000);

  it("keeps broker authoring neutral while sending Azure Service Bus configuration", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.click("text=New Source");
    const form = formNamed(view, "Create a Source");
    await choose(form.getByLabel("Connector"), /HTTP/);
    await choose(form.getByLabel("Topic"), /Orders/);
    await choose(form.getByLabel("Type"), "Message broker");
    await form.getByLabel("Name", { exact: true }).fill("broker-intake");
    await form.getByRole("heading", { name: "Message broker input" }).waitFor();
    await form.getByRole("heading", { name: "Event Normalization" }).waitFor();
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    const builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    await builder.getByRole("heading", { name: "Sample message" }).waitFor();
    expect(await builder.getByText("Request headers").count()).toBe(0);
    await builder.getByLabel("Message body (JSON)").waitFor();
    // A broker message has no request headers, so there is nothing to read an Event type from but
    // the body — the choice a webhook gets is not offered here.
    await builder.getByRole("radio", { name: "From input" }).click();
    expect(await builder.getByLabel("Read from").count()).toBe(0);
    await builder.getByLabel("Event type field").waitFor();
    await builder.getByRole("radio", { name: "Fixed value" }).click();
    await builder.getByLabel("Event identity").selectOption("message_id");
    await expect.poll(() => builder.getByRole("button", { name: "Use configuration" }).isEnabled()).toBe(true);
    await builder.getByRole("button", { name: "Use configuration" }).click();
    expect(await form.getByLabel(/^Verification/).count()).toBe(0);
    expect(await form.getByLabel("Broker type").textContent()).toContain("Azure Service Bus");
    await form.getByLabel("Namespace").fill("acme.servicebus.windows.net");
    await form.getByLabel("Queue name").fill("orders");
    await form.getByLabel("Event type 1", { exact: true }).fill("order.created");
    await view.click("text=Create Source");

    const sent = await submitted(writes);
    expect(sent.body.type).toBe("broker");
    expect(sent.body.configuration).toEqual({
      transport: "azure_service_bus",
      authentication: { scheme: "azure_identity" },
      transport_config: { namespace: "acme.servicebus.windows.net", queue_name: "orders" },
    });
    expect(sent.body.verification).toBeNull();
    expect(sent.body.event_identity_rule).toEqual({
      kind: "message_id",
      value: "message_id",
      allow_missing: false,
    });
    await view.close();
  }, 60_000);
});

describe("Update and deactivate, driven through a real browser", () => {
  // Tenant, Topic and Source updates send only plain strings and are covered by the jsdom suite.
  // What is exercised here is what a string-only form has to convert: parsed configuration
  // documents, plus the two shapes with no other coverage at all — a confirmed deactivate and the
  // one DELETE the dashboard issues.

  it("sends an updated Destination from guided configuration and preserves authentication", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/destinations/${destinationId}`);

    // Editing is a deliberate act now rather than the panel's resting state, and the form names
    // itself instead of repeating on screen the heading its disclosure already carries.
    await view.getByRole("button", { name: "Edit", exact: true }).click();
    await formNamed(view, "Edit sink").getByLabel("Base URI").fill("http://moved.invalid");
    await view.click("text=Save changes");

    const sent = await submitted(writes);
    expect(sent.method).toBe("PUT");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/destinations/${destinationId}`);
    expect(sent.body.configuration).toEqual({ base_uri: "http://moved.invalid" });
    expect(sent.body.authentication).toBeNull();
    await view.close();
  }, 60_000);

  it("changes and removes Destination authentication through declared fields", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/destinations/${destinationId}`);
    let current = {
      ...destinationDetail,
      authentication: { scheme: "bearer_token", config: {}, secret_refs: { token: "old-token" } },
    };
    await view.route(`**/admin/tenants/${tenantId}/destinations/${destinationId}`, (route) => {
      const request = route.request();
      if (request.method() === "PUT") {
        writes.push(request);
        current = { ...current, ...request.postDataJSON() };
      }
      return route.fulfill({ json: current });
    });
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    let form = formNamed(view, "Edit sink");
    await choose(form.getByRole("combobox", { name: "Authentication" }), "API key header");
    await form.getByLabel("Header name").fill("X-Api-Key");
    await form.getByLabel("API key secret reference").fill("new-token");
    await form.getByRole("button", { name: "Save changes" }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0].postDataJSON().authentication).toEqual({
      scheme: "api_key_header",
      config: { header_name: "X-Api-Key" },
      secret_refs: { api_key: "new-token" },
    });

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    form = formNamed(view, "Edit sink");
    await choose(form.getByRole("combobox", { name: "Authentication" }), "No authentication");
    await form.getByRole("button", { name: "Save changes" }).click();
    await expect.poll(() => writes.length).toBe(2);
    expect(writes[1].postDataJSON().authentication).toBeNull();
    await view.close();
  }, 60_000);

  it("preserves raw Destination documents that guided fields cannot round-trip", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/destinations/${destinationId}`);
    await view.route(`**/admin/connectors/${connectorId}`, (route) =>
      route.fulfill({
        json: {
          ...connectorDetail,
          manifest: {
            ...connectorDetail.manifest,
            destination_configuration_schema: {
              type: "object",
              properties: {
                base_uri: { type: "string", format: "uri" },
                mode: { type: "string", enum: ["fast", "safe"] },
              },
              required: ["base_uri", "mode"],
              additionalProperties: false,
            },
          },
        },
      }),
    );
    const raw = {
      ...destinationDetail,
      configuration: { base_uri: "http://sink.invalid", provider_option: true },
      authentication: {
        scheme: "bearer_token",
        config: { audience: "orders" },
        secret_refs: { token: "sink-token" },
      },
    };
    await view.route(`**/admin/tenants/${tenantId}/destinations/${destinationId}`, (route) => {
      if (route.request().method() === "PUT") return route.fallback();
      return route.fulfill({ json: raw });
    });
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    const form = formNamed(view, "Edit sink");
    await form.getByLabel("Raw configuration (JSON)").waitFor();
    await form.getByLabel("Raw authentication (JSON)").waitFor();
    expect(await form.getByLabel("Base URI").count()).toBe(0);
    await form.getByRole("button", { name: "Save changes" }).click();

    const sent = await submitted(writes);
    expect(sent.body.configuration).toEqual(raw.configuration);
    expect(sent.body.authentication).toEqual(raw.authentication);
    await view.close();
  }, 60_000);

  it("corrects, clears, and adds back a Source Event identity", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources/${sourceId}`);
    let identity: Record<string, unknown> | null = {
      kind: "header",
      value: "X-Wrong-Delivery",
      allow_missing: false,
    };
    let revision = 0;
    await view.route(`**/admin/tenants/${tenantId}/sources/${sourceId}`, (route) => {
      const request = route.request();
      if (request.method() === "PUT") {
        writes.push(request);
        identity = request.postDataJSON().event_identity_rule;
        revision += 1;
      }
      return route.fulfill({
        status: 200,
        json: {
          ...sourceDetail,
          type: "webhook",
          configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
          verification: null,
          input_requirements: null,
          mapping: null,
          event_identity_rule: identity,
          updated_at: `2026-09-15T00:00:0${revision}Z`,
        },
      });
    });
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    let form = formNamed(view, "Edit Webhook Source");
    // The stored rule is stated where the Source is authored, without opening the Builder.
    await form.getByText("X-Wrong-Delivery", { exact: true }).waitFor();
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    let builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    // A value this sample does not carry stays selected under its own name rather than being cleared;
    // the verdict, not the picker, says what its absence means.
    expect(await builder.getByLabel("Identity header").inputValue()).toBe("X-Wrong-Delivery");
    expect(await builder.getByLabel("Identity header").locator("option:checked").textContent()).toBe(
      "X-Wrong-Delivery",
    );
    await builder.getByLabel("Header 1 name").fill("X-GitHub-Delivery");
    await builder.getByLabel("Header 1 sample value").fill("d-1");
    await builder.getByLabel("Identity header").selectOption("x-github-delivery");
    await builder.getByRole("button", { name: "Use configuration" }).click();
    await form.getByRole("button", { name: "Save configuration" }).click();
    expect((await submitted(writes)).body.event_identity_rule).toEqual({
      kind: "header",
      value: "x-github-delivery",
      allow_missing: false,
    });
    writes.length = 0;

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    form = formNamed(view, "Edit Webhook Source");
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    await builder.getByLabel("Event identity").selectOption("");
    await builder.getByRole("button", { name: "Use configuration" }).click();
    await form.getByText("No duplicate detection").waitFor();
    await form.getByRole("button", { name: "Save configuration" }).click();
    expect((await submitted(writes)).body.event_identity_rule).toBeNull();
    writes.length = 0;

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    form = formNamed(view, "Edit Webhook Source");
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    await builder.getByLabel("Header 1 name").fill("X-Provider-Delivery");
    await builder.getByLabel("Header 1 sample value").fill("d-2");
    await builder.getByLabel("Event identity").selectOption("header");
    await builder.getByLabel("Identity header").selectOption("x-provider-delivery");
    await builder.getByLabel("Accept a request that carries no value here").check();
    await builder.getByRole("button", { name: "Use configuration" }).click();
    await form.getByRole("button", { name: "Save configuration" }).click();
    expect((await submitted(writes)).body.event_identity_rule).toEqual({
      kind: "header",
      value: "x-provider-delivery",
      allow_missing: true,
    });
    await view.close();
  }, 60_000);

  it("changes, removes with confirmation, and adds Source verification", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources/${sourceId}`);
    let verification: Record<string, unknown> | null = {
      scheme: "hmac_sha256",
      config: { header: "X-Signature" },
      secret_refs: { secret: "old-hook", secondary: "preserved" },
    };
    let revision = 0;
    await view.route(`**/admin/connectors/${connectorId}`, (route) =>
      route.fulfill({
        json: {
          ...connectorDetail,
          manifest: {
            ...connectorDetail.manifest,
            source_verification: {
              allow_unverified: true,
              schemes: [{ scheme: "hmac_sha256" }, { scheme: "webhook_signature" }],
            },
          },
        },
      }),
    );
    await view.route(`**/admin/tenants/${tenantId}/sources/${sourceId}`, (route) => {
      const request = route.request();
      if (request.method() === "PUT") {
        writes.push(request);
        verification = request.postDataJSON().verification;
        revision += 1;
      }
      return route.fulfill({
        status: 200,
        json: {
          ...sourceDetail,
          type: "webhook",
          configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
          verification,
          input_requirements: null,
          mapping: null,
          event_identity_rule: null,
          updated_at: `2026-09-15T00:01:0${revision}Z`,
        },
      });
    });
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    let form = formNamed(view, "Edit Webhook Source");
    expect(await form.getByLabel("Secret reference").inputValue()).toBe("old-hook");
    await form.getByRole("combobox", { name: "Verification" }).click();
    expect(await view.getByRole("option").allInnerTexts()).toEqual([
      "No verification",
      "HMAC SHA-256",
      "webhook_signature",
    ]);
    await view.getByRole("option", { name: "webhook_signature" }).click();
    await form.getByLabel("Secret reference").fill("new-hook");
    await form.getByRole("button", { name: "Save configuration" }).click();
    expect((await submitted(writes)).body.verification).toEqual({
      scheme: "webhook_signature",
      config: {},
      secret_refs: { secret: "new-hook" },
    });
    writes.length = 0;

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    form = formNamed(view, "Edit Webhook Source");
    await choose(form.getByRole("combobox", { name: "Verification" }), "No verification");
    expect(await form.getByRole("button", { name: "Save configuration" }).count()).toBe(0);
    await form.getByLabel("Name", { exact: true }).press("Enter");
    expect(writes).toHaveLength(0);
    await form.getByRole("button", { name: "Remove verification and save" }).click();
    const confirmation = view.getByRole("dialog", { name: "Remove verification and save" });
    expect(await confirmation.textContent()).toContain("start accepting unsigned requests");
    await confirmation.getByRole("button", { name: "Remove verification and save" }).click();
    expect((await submitted(writes)).body.verification).toBeNull();
    writes.length = 0;

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    form = formNamed(view, "Edit Webhook Source");
    await choose(form.getByRole("combobox", { name: "Verification" }), "HMAC SHA-256");
    await form.getByLabel("Secret reference").fill("restored-hook");
    await form.getByRole("button", { name: "Save configuration" }).click();
    expect((await submitted(writes)).body.verification).toEqual({
      scheme: "hmac_sha256",
      config: {},
      secret_refs: { secret: "restored-hook" },
    });
    await view.close();
  }, 60_000);

  it("reports a rejected verification write above the form", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/sources/${sourceId}`);
    await view.route(`**/admin/connectors/${connectorId}`, (route) =>
      route.fulfill({
        json: {
          ...connectorDetail,
          manifest: {
            ...connectorDetail.manifest,
            source_verification: { allow_unverified: false, schemes: [{ scheme: "hmac_sha256" }] },
          },
        },
      }),
    );
    await view.route(`**/admin/tenants/${tenantId}/sources/${sourceId}`, (route) => {
      const request = route.request();
      if (request.method() === "PUT")
        return route.fulfill({
          status: 400,
          contentType: "application/problem+json",
          json: { title: "The Source was rejected.", errors: { verification: ["The secret cannot be used."] } },
        });
      return route.fulfill({
        json: {
          ...sourceDetail,
          type: "webhook",
          configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
          verification: { scheme: "hmac_sha256", config: {}, secret_refs: { secret: "old-hook" } },
          input_requirements: null,
          mapping: null,
          event_identity_rule: null,
        },
      });
    });
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    const form = formNamed(view, "Edit Webhook Source");
    await form.getByRole("combobox", { name: "Verification" }).click();
    expect(await view.getByRole("option").allInnerTexts()).toEqual(["HMAC SHA-256"]);
    await view.keyboard.press("Escape");
    await form.getByLabel("Secret reference").fill("missing-hook");
    await form.getByRole("button", { name: "Save configuration" }).click();
    expect(await form.getByRole("alert").textContent()).toContain("The secret cannot be used.");
    expect(await form.getByLabel("Configuration (JSON)").isVisible()).toBe(false);
    await view.close();
  }, 60_000);

  it.each([
    [
      "an extended broker document",
      "broker",
      {
        transport: "azure_service_bus",
        authentication: { scheme: "azure_identity" },
        transport_config: { namespace: "acme.servicebus.windows.net", queue_name: "orders" },
        consumer_options: { prefetch: 20 },
      },
      "raw",
    ],
    [
      "an unrecognised broker transport",
      "broker",
      {
        transport: "rabbitmq",
        authentication: { scheme: "connection_string", secret_ref: "rabbit" },
        transport_config: { queue_name: "orders" },
      },
      "raw",
    ],
    [
      "a represented broker document",
      "broker",
      {
        transport: "azure_service_bus",
        authentication: { scheme: "azure_identity" },
        transport_config: { namespace: "acme.servicebus.windows.net", queue_name: "orders" },
      },
      "guided",
    ],
    ["a webhook document", "webhook", { callback_id: "66666666-6666-6666-6666-666666666666" }, "none"],
    ["an Event API document", "event_api", { region: "eu" }, "none"],
  ] as const)(
    "preserves %s with one configuration writer",
    async (_case, type, configuration, editor) => {
      const { page: view, writes } = await open(`/tenants/${tenantId}/sources/${sourceId}`);
      await view.route(`**/admin/tenants/${tenantId}/sources/${sourceId}`, (route) => {
        const request = route.request();
        if (request.method() === "PUT") writes.push(request);
        return route.fulfill({
          status: 200,
          json: {
            ...sourceDetail,
            type,
            configuration,
            verification: null,
            input_requirements: null,
            mapping: null,
            event_identity_rule: null,
          },
        });
      });
      await view.reload();

      await view.getByRole("button", { name: "Edit", exact: true }).click();
      const label = type === "broker" ? "Message broker" : type === "event_api" ? "Event API" : "Webhook";
      const form = formNamed(view, `Edit ${label} Source`);
      let expectedConfiguration: Record<string, unknown> = configuration;
      expect(await form.getByLabel("Broker type").count()).toBe(editor === "guided" ? 1 : 0);
      expect(await form.getByRole("heading", { name: "Raw broker configuration" }).count()).toBe(
        editor === "raw" ? 1 : 0,
      );
      if (editor === "raw") {
        const raw = form.getByLabel("Broker configuration (JSON)");
        expect(JSON.parse(await raw.inputValue())).toEqual(configuration);
        expectedConfiguration = { ...configuration, edited_as_raw: true };
        await raw.fill(JSON.stringify(expectedConfiguration));
      }
      await form.getByRole("button", { name: "Save configuration" }).click();
      expect((await submitted(writes)).body.configuration).toEqual(expectedConfiguration);
      await view.close();
    },
    60_000,
  );

  it("preserves an advanced Source mapping until the Operator replaces it in the Builder", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources/${sourceId}`);
    const expression = '$merge([{"event_type": "legacy"}, {"payload": $}])';
    const inputRequirements = {
      type: "object",
      properties: { order_id: { type: "string" } },
      required: ["order_id"],
      additionalProperties: true,
    };
    await view.route(`**/admin/tenants/${tenantId}/sources/${sourceId}`, (route) => {
      const request = route.request();
      if (request.method() === "PUT") writes.push(request);
      return route.fulfill({
        status: 200,
        json: {
          ...sourceDetail,
          type: "webhook",
          configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
          verification: null,
          input_requirements: inputRequirements,
          mapping: { engine: "jsonata", version: "1", expression },
          event_identity_rule: null,
        },
      });
    });
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    const form = formNamed(view, "Edit Webhook Source");
    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    let builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    await builder.getByRole("heading", { name: "Advanced JSONata" }).waitFor();
    expect(await builder.getByLabel("Source mapping expression").inputValue()).toBe(expression);
    await builder.getByRole("button", { name: "Back to Source" }).click();

    await form.getByText("Raw event contract", { exact: true }).click();
    await form.getByText("Changes replace Event Builder output.", { exact: false }).waitFor();
    expect(await form.getByLabel("Event mapping (JSONata, optional)").inputValue()).toBe(expression);

    await form.getByRole("button", { name: "Open Integrios Event Builder" }).click();
    builder = view.getByRole("dialog", { name: "Integrios Event Builder" });
    await builder.getByRole("button", { name: "Reset to guided" }).click();
    await view
      .getByRole("dialog", { name: "Reset to guided" })
      .getByRole("button", { name: "Reset to guided" })
      .click();
    await builder.getByLabel("Request body (JSON)").fill('{"order_id":"A-42"}');
    await builder.getByLabel("Event type").fill("orders");
    await expect.poll(() => builder.getByRole("button", { name: "Use configuration" }).isEnabled()).toBe(true);
    await builder.getByRole("button", { name: "Use configuration" }).click();
    await form.getByRole("button", { name: "Save configuration" }).click();
    // The mapping changed, so saving names what routes on this Topic by Event type first.
    const confirm = view.getByRole("dialog", { name: "Save configuration" });
    await confirm.getByText(/to-sink \(order\.created\)/).waitFor();
    expect(writes.some((request) => request.method() === "PUT")).toBe(false);
    await confirm.getByRole("button", { name: "Save configuration" }).click();

    const sent = await submitted(writes);
    expect(sent.body.mapping).toEqual({
      engine: "jsonata",
      version: "1",
      expression: expect.stringContaining('"orders"'),
    });
    expect(sent.body.input_requirements).toEqual(inputRequirements);
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
    expect(await form.getByLabel("Order", { exact: true }).count()).toBe(0);
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
    expect(sent.body.event_types).toEqual(["order.created"]);
    await view.close();
  }, 60_000);

  it("adds and removes the guided Subscription response success rule", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`);
    let current = { ...subscriptionDetail, http_success: null };
    await view.route(`**/admin/tenants/${tenantId}/topics/${topicId}/subscriptions/${subscriptionId}`, (route) => {
      const request = route.request();
      if (request.method() === "PUT") {
        writes.push(request);
        current = { ...current, ...request.postDataJSON() };
      }
      return route.fulfill({ json: current });
    });
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    let form = formNamed(view, "Edit to-sink");
    await choose(form.getByLabel("Success check"), "Response JSON boolean");
    await form.getByLabel("Boolean field").fill("ok");
    await choose(form.getByLabel("Expected value"), "False");
    await form.getByRole("button", { name: "Save changes" }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0].postDataJSON().http_success).toEqual({
      evaluator: "json_boolean",
      field: "ok",
      expected: false,
    });

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    form = formNamed(view, "Edit to-sink");
    await choose(form.getByLabel("Success check"), "Any HTTP 2xx response");
    await form.getByRole("button", { name: "Save changes" }).click();
    await expect.poll(() => writes.length).toBe(2);
    expect(writes[1].postDataJSON().http_success).toBeNull();
    await view.close();
  }, 60_000);

  it("preserves a Subscription mapping document the Playground cannot round-trip", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`);
    const mapping = {
      engine: "jsonata",
      version: "1",
      expression: '{"id": id}',
      extension: true,
    };
    await view.route(`**/admin/tenants/${tenantId}/topics/${topicId}/subscriptions/${subscriptionId}`, (route) => {
      if (route.request().method() === "PUT") return route.fallback();
      return route.fulfill({ json: { ...subscriptionDetail, mapping_config: mapping } });
    });
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).click();
    const form = formNamed(view, "Edit to-sink");
    await form.getByLabel("Raw mapping (JSON)").waitFor();
    expect(await form.getByRole("button", { name: /Playground/ }).count()).toBe(0);
    await form.getByRole("button", { name: "Save changes" }).click();

    const sent = await submitted(writes);
    expect(sent.body.mapping).toEqual(mapping);
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
        json: { ...subscriptionDetail, event_types: ["order's.placed"] },
      }),
    );
    await view.reload();

    await view.getByRole("link", { name: /orders-intake/ }).click();
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
    await advanced.page.getByRole("link", { name: /orders-intake/ }).click();
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

  /// A Topic has no status: it groups its Sources and Subscriptions and exists until deleted.
  it("offers no status action on a Topic", async () => {
    const { page: view } = await open(`/tenants/${tenantId}/topics/${topicId}`);
    await view.getByRole("button", { name: "Edit", exact: true }).waitFor();

    for (const action of ["Activate", "Deactivate"])
      expect(await view.getByRole("button", { name: action, exact: true }).count()).toBe(0);
    await view.close();
  }, 60_000);

  it("deactivates a Source only after the confirmation naming it", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources/${sourceId}`);

    await view.getByRole("button", { name: "Deactivate", exact: true }).click();
    expect(writes, "Deactivating ran before it was confirmed.").toHaveLength(0);
    await view.click("text=Deactivate orders-intake");

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/sources/${sourceId}/deactivate`);
    await view.close();
  }, 60_000);

  /// Inactive is reversible configuration: the Source stays editable and is activated again directly.
  it("keeps an Inactive Source editable and activates it again", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources/${sourceId}`);
    await view.route(`**/admin/tenants/${tenantId}/sources/${sourceId}`, (route) =>
      route.fulfill({ status: 200, json: { ...sourceDetail, status: "inactive" } }),
    );
    await view.reload();

    await view.getByRole("button", { name: "Edit", exact: true }).waitFor();
    expect(await view.getByRole("button", { name: "Deactivate", exact: true }).count()).toBe(0);
    await view.getByRole("button", { name: "Activate", exact: true }).click();

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/sources/${sourceId}/activate`);
    await view.close();
  }, 60_000);
});
