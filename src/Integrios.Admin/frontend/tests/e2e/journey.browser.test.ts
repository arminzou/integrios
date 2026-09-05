// @vitest-environment node

import { type Browser, chromium, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

/// One golden journey against a real deployment: author a Tenant, a Connection, a Topic, a
/// Subscription and a Source, each through its own screen, and let the real Admin API judge every
/// body the dashboard builds.
///
/// The other browser tests answer the API themselves, so they prove the dashboard sends what was
/// intended and nothing about whether the server accepts it. This one proves acceptance, which is
/// the only question a stub can never answer.
///
/// Two things it deliberately does not exercise. Sign-in: the session bootstrap is stubbed and the
/// requests carry an OperatorKey, so the cookie and antiforgery path is not what runs here — that
/// is covered against a real identity provider in the Functional tests. And the shell: the UI is
/// served by Vite so the journey runs against current source, while the API is the packaged one.
///
/// Opt-in, because it needs a deployment:
///   INTEGRIOS_JOURNEY_ORIGIN=http://localhost:5150 \
///   INTEGRIOS_JOURNEY_OPERATOR_KEY='OperatorKey global_operator_key:...' \
///   npx vitest run tests/e2e/journey.browser.test.ts
const adminOrigin = process.env.INTEGRIOS_JOURNEY_ORIGIN;
const operatorKey = process.env.INTEGRIOS_JOURNEY_OPERATOR_KEY;
const configured = Boolean(adminOrigin && operatorKey);

const session = {
  user_id: "55555555-5555-5555-5555-555555555555",
  display_name: "Journey",
  email: null,
  antiforgery_token: "unused",
  antiforgery_header_name: "X-Integrios-Antiforgery",
  antiforgery_form_field_name: "__antiforgery",
};

const run = `j${Date.now().toString(36)}`;

let server: ViteDevServer;
let browser: Browser;
let origin: string;

beforeAll(async () => {
  if (!configured) return;
  server = await createServer({ server: { host: "127.0.0.1", port: 0 } });
  await server.listen();
  const address = server.httpServer!.address();
  if (address === null || typeof address === "string") throw new Error("The dev server exposed no port.");
  origin = `http://127.0.0.1:${address.port}`;
  browser = await chromium.launch();
}, 120_000);

afterAll(async () => {
  await browser?.close();
  await server?.close();
});

/// Every write the journey makes, with the answer the deployment gave. A failure then names the
/// call that was refused instead of only the navigation that never happened.
const writes: string[] = [];

async function openDashboard(path: string): Promise<Page> {
  const page = await browser.newPage();
  await page.route("**/auth/session", (route) => route.fulfill({ json: session }));

  // Every API call is replayed against the real deployment and its answer returned verbatim.
  // Playwright performs the call itself, so the page never makes a cross-origin request and the
  // dashboard stays unaware that its API lives on another port.
  await page.route("**/admin/**", async (route) => {
    const request = route.request();
    const source = new URL(request.url());
    const response = await route.fetch({
      url: `${adminOrigin}${source.pathname}${source.search}`,
      headers: { ...request.headers(), authorization: operatorKey! },
    });
    if (request.method() !== "GET")
      writes.push(
        `${request.method()} ${source.pathname} -> ${response.status()} ${(await response.text()).slice(0, 200)}`,
      );
    await route.fulfill({ response });
  });

  await page.goto(`${origin}${path}`);
  await page.getByRole("heading", { level: 1 }).waitFor();
  return page;
}

/// Waits for the screen a successful create navigates to, and returns the new id. On failure it
/// reports the calls the deployment actually answered, so a refused body names itself instead of
/// surfacing as a navigation that never happened.
async function created(page: Page, pattern: RegExp, what: string): Promise<string> {
  try {
    await page.waitForURL(pattern, { timeout: 15_000 });
  } catch {
    throw new Error([`The ${what} was not created.`, "Writes the deployment answered:", ...writes].join("\n"));
  }
  return new URL(page.url()).pathname.split("/").pop()!;
}

async function closeView(page: Page): Promise<void> {
  await page.unrouteAll({ behavior: "ignoreErrors" });
  await page.close();
}

/// Reads the deployment directly, to confirm what the journey wrote actually landed.
async function readAdmin(path: string): Promise<Record<string, unknown>> {
  const response = await fetch(`${adminOrigin}${path}`, { headers: { authorization: operatorKey! } });
  if (!response.ok) throw new Error(`GET ${path} answered ${response.status}.`);
  return (await response.json()) as Record<string, unknown>;
}

describe.skipIf(!configured)("A golden authoring journey against a real deployment", () => {
  it("authors a Tenant, Connection, Topic, Subscription and Source through their own screens", async () => {
    const connectors = (await readAdmin("/admin/connectors?limit=1")).items as { id: string }[];
    expect(connectors.length, "The deployment has no Connector to build a Connection on.").toBeGreaterThan(0);

    // Tenant.
    let view = await openDashboard("/tenants");
    await view.click("text=New Tenant");
    await view.fill("#create-tenant-slug", run);
    await view.fill("#create-tenant-name", `Journey ${run}`);
    await view.click("text=Create Tenant");
    const tenantId = await created(view, /\/tenants\/[0-9a-f-]{36}$/, "Tenant");
    await closeView(view);

    // Connection.
    view = await openDashboard(`/tenants/${tenantId}/connections`);
    await view.click("text=New Connection");
    await view.selectOption("#create-connection-connector", connectors[0].id);
    await view.fill("#create-connection-name", `${run}-sink`);
    await view.fill("#create-connection-config", '{"base_uri":"http://mocksink:8080"}');
    await view.click("text=Create Connection");
    const connectionId = await created(view, /\/connections\/[0-9a-f-]{36}$/, "Connection");
    await closeView(view);

    // Topic.
    view = await openDashboard(`/tenants/${tenantId}/topics`);
    await view.click("text=New Topic");
    await view.fill("#create-topic-name", `${run}-orders`);
    await view.click("text=Create Topic");
    const topicId = await created(view, /\/topics\/[0-9a-f-]{36}$/, "Topic");

    // Subscription, authored on the Topic it belongs to.
    await view.click("text=New Subscription");
    await view.fill("#create-subscription-name", `${run}-to-sink`);
    await view.selectOption("#create-subscription-destination", connectionId);
    await view.fill("#create-subscription-match-rules", `{"event_type":"${run}.created"}`);
    await view.click("text=Create Subscription");
    await created(view, /\/subscriptions\/[0-9a-f-]{36}$/, "Subscription");
    await closeView(view);

    // Source.
    view = await openDashboard(`/tenants/${tenantId}/sources`);
    await view.click("text=New Source");
    await view.selectOption("#create-source-connection", connectionId);
    await view.selectOption("#create-source-topic", topicId);
    await view.selectOption("#create-source-type", "event_api");
    await view.fill("#create-source-configuration", '{"source_contract":"event_json"}');
    await view.click("text=Create Source");
    await created(view, /\/sources\/[0-9a-f-]{36}$/, "Source");
    await closeView(view);

    // Everything the journey authored is readable from the deployment, not merely echoed by a form.
    const [connections, topics, sources, subscriptions] = await Promise.all([
      readAdmin(`/admin/tenants/${tenantId}/connections`),
      readAdmin(`/admin/tenants/${tenantId}/topics`),
      readAdmin(`/admin/tenants/${tenantId}/sources`),
      readAdmin(`/admin/tenants/${tenantId}/topics/${topicId}/subscriptions`),
    ]);

    expect((connections.items as unknown[]).length).toBe(1);
    expect((topics.items as unknown[]).length).toBe(1);
    expect((sources.items as unknown[]).length).toBe(1);
    expect((subscriptions.items as { name: string }[])[0].name).toBe(`${run}-to-sink`);
  }, 180_000);
});
