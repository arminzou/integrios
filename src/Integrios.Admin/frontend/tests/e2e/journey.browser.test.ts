// @vitest-environment node

import { type Browser, chromium, type Locator, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

/// One golden journey against a real deployment: author a Tenant, a Destination, a Topic, a
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

/// A screen can carry more than one form, and its filter bar shares labels with them, so a control
/// is addressed by its label within the form that owns it — the same handle the stubbed browser
/// tests use. A form is named by its accessible name, not by text it happens to show.
function formNamed(page: Page, name: string) {
  return page.getByRole("form", { name });
}

/// The pickers are a scripted listbox; its options render outside the form that owns the trigger.
async function choose(control: Locator, option: string) {
  await control.click();
  await control.page().getByRole("option", { name: option, exact: true }).click();
}

/// Reads the deployment directly, to confirm what the journey wrote actually landed.
async function readAdmin(path: string): Promise<Record<string, unknown>> {
  const response = await fetch(`${adminOrigin}${path}`, { headers: { authorization: operatorKey! } });
  if (!response.ok) throw new Error(`GET ${path} answered ${response.status}.`);
  return (await response.json()) as Record<string, unknown>;
}

/// The status the deployment reports for a resource, polled until the action the page sent lands.
async function expectStatus(path: string, status: string) {
  await expect.poll(async () => (await readAdmin(path)).status, { timeout: 15_000 }).toBe(status);
}

/// Activate is a plain button; Deactivate and Delete ask first, and the confirmation names the
/// resource, so pressing it proves the question was about the right one.
async function act(page: Page, action: "Activate" | "Deactivate" | "Delete", name: string) {
  await page.getByRole("button", { name: action, exact: true }).click();
  if (action !== "Activate")
    await page
      .getByRole("dialog")
      .getByRole("button", { name: `${action} ${name}`, exact: true })
      .click();
}

describe.skipIf(!configured)("A golden authoring journey against a real deployment", () => {
  it("authors a Tenant, Destination, Topic, Subscription and Source through their own screens", async () => {
    const connectors = (await readAdmin("/admin/connectors?limit=100")).items as {
      id: string;
      key: string;
      name: string;
      contract_version: number;
      direction: "source" | "destination" | "both";
    }[];
    // The journey authors an unverified Source and an unauthenticated Destination, which the generic
    // `http` example permits and a Connector declaring required schemes refuses. The list is newest
    // first, so without this preference the choice would follow whatever was applied last.
    const preferred = [...connectors].sort((a, b) => Number(b.key === "http") - Number(a.key === "http"));
    const sourceConnector = preferred.find(({ direction }) => direction === "source" || direction === "both");
    const destinationConnector = preferred.find(({ direction }) => direction === "destination" || direction === "both");
    expect(sourceConnector, "The deployment has no source-capable Connector.").toBeDefined();
    expect(destinationConnector, "The deployment has no destination-capable Connector.").toBeDefined();
    if (!sourceConnector || !destinationConnector) throw new Error("No compatible Connector is available.");

    // Tenant.
    let view = await openDashboard("/tenants");
    await view.click("text=New Tenant");
    const tenantForm = formNamed(view, "Create a Tenant");
    await tenantForm.getByLabel("Slug", { exact: true }).fill(run);
    await tenantForm.getByLabel("Name", { exact: true }).fill(`Journey ${run}`);
    await view.click("text=Create Tenant");
    const tenantId = await created(view, /\/tenants\/[0-9a-f-]{36}$/, "Tenant");
    await closeView(view);

    // Destination. Its picker already contains only destination-capable Connectors.
    view = await openDashboard(`/tenants/${tenantId}/destinations`);
    await view.click("text=New Destination");
    const destinationForm = formNamed(view, "Create a Destination");
    const { name, contract_version } = destinationConnector;
    await choose(destinationForm.getByLabel("Connector", { exact: true }), `${name} (v${contract_version})`);
    await destinationForm.getByLabel("Name", { exact: true }).fill(`${run}-sink`);
    await destinationForm.getByLabel("Base URI", { exact: true }).fill("http://mocksink:8080");
    await view.click("text=Create Destination");
    const destinationId = await created(view, /\/destinations\/[0-9a-f-]{36}$/, "Destination");
    await closeView(view);

    // Topic.
    view = await openDashboard(`/tenants/${tenantId}/topics`);
    await view.click("text=New Topic");
    await formNamed(view, "Create a Topic").getByLabel("Key", { exact: true }).fill(`${run}-orders`);
    await view.click("text=Create Topic");
    const topicId = await created(view, /\/topics\/[0-9a-f-]{36}$/, "Topic");
    await closeView(view);

    // Source, declaring the Event types it publishes before any traffic exists.
    view = await openDashboard(`/tenants/${tenantId}/sources`);
    await view.click("text=New Source");
    const sourceForm = formNamed(view, "Create a Source");
    await choose(sourceForm.getByLabel("Connector", { exact: true }), sourceConnector.name);
    await choose(sourceForm.getByLabel("Topic", { exact: true }), `${run}-orders`);
    await choose(sourceForm.getByLabel("Type", { exact: true }), "Event API");
    await sourceForm.getByLabel("Name", { exact: true }).fill(`${run}-intake`);
    await sourceForm.getByLabel("Event type 1", { exact: true }).fill(`${run}.created`);
    await sourceForm.getByRole("button", { name: "Add Event type" }).click();
    await sourceForm.getByLabel("Event type 2", { exact: true }).fill(`${run}.shipped`);
    await view.click("text=Create Source");
    const sourceId = await created(view, /\/sources\/[0-9a-f-]{36}$/, "Source");
    await view.getByRole("dialog", { name: "Publish through this Source" }).waitFor();
    await closeView(view);

    // The Topic shows what its Source declares, and the Subscription chooses from exactly that.
    view = await openDashboard(`/tenants/${tenantId}/topics/${topicId}`);
    const topicDetail = view.getByRole("complementary", { name: "Topic detail" });
    await topicDetail.getByText(`${run}.shipped`, { exact: true }).waitFor();
    await view.click("text=Manage Subscriptions");
    await view.click("text=New Subscription");
    const subscriptionForm = formNamed(view, "Create a Subscription");
    await subscriptionForm.getByLabel("Name", { exact: true }).fill(`${run}-to-sink`);
    await choose(subscriptionForm.getByLabel("Destination", { exact: true }), `${run}-sink`);
    const selection = subscriptionForm.getByRole("group", { name: "Event types" });
    await selection.getByRole("checkbox", { name: `${run}.created` }).check();
    await selection.getByRole("checkbox", { name: `${run}.shipped` }).check();
    expect(await selection.getByRole("textbox").count()).toBe(0);
    await view.click("text=Create Subscription");
    const subscriptionId = await created(view, /\/subscriptions\/[0-9a-f-]{36}\/[0-9a-f-]{36}$/, "Subscription");
    await closeView(view);

    // Everything the journey authored is readable from the deployment, not merely echoed by a form.
    const [destinations, topics, sources, subscriptions] = await Promise.all([
      readAdmin(`/admin/tenants/${tenantId}/destinations`),
      readAdmin(`/admin/tenants/${tenantId}/topics`),
      readAdmin(`/admin/tenants/${tenantId}/sources`),
      readAdmin(`/admin/tenants/${tenantId}/topics/${topicId}/subscriptions`),
    ]);

    expect((destinations.items as unknown[]).length).toBe(1);
    expect((topics.items as unknown[]).length).toBe(1);
    expect((sources.items as unknown[]).length).toBe(1);
    expect((subscriptions.items as { name: string }[])[0].name).toBe(`${run}-to-sink`);
    expect((topics.items as { event_types: string[] }[])[0].event_types).toEqual([`${run}.created`, `${run}.shipped`]);

    // Lifecycle. A new Source and Subscription start Inactive, so nothing flows until the Operator
    // activates each one; both then deactivate and return to Inactive without being deleted.
    const sourcePath = `/admin/tenants/${tenantId}/sources/${sourceId}`;
    const subscriptionPath = `/admin/tenants/${tenantId}/topics/${topicId}/subscriptions/${subscriptionId}`;
    const destinationPath = `/admin/tenants/${tenantId}/destinations/${destinationId}`;
    await expectStatus(sourcePath, "inactive");
    await expectStatus(subscriptionPath, "inactive");

    view = await openDashboard(`/tenants/${tenantId}/sources/${sourceId}`);
    await act(view, "Activate", `${run}-intake`);
    await expectStatus(sourcePath, "active");
    await closeView(view);

    view = await openDashboard(`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`);
    await act(view, "Activate", `${run}-to-sink`);
    await expectStatus(subscriptionPath, "active");
    await act(view, "Deactivate", `${run}-to-sink`);
    await expectStatus(subscriptionPath, "inactive");
    await closeView(view);

    view = await openDashboard(`/tenants/${tenantId}/destinations/${destinationId}`);
    await act(view, "Deactivate", `${run}-sink`);
    await expectStatus(destinationPath, "inactive");
    await act(view, "Activate", `${run}-sink`);
    await expectStatus(destinationPath, "active");
    await closeView(view);

    view = await openDashboard(`/tenants/${tenantId}`);
    await act(view, "Deactivate", `Journey ${run}`);
    await expectStatus(`/admin/tenants/${tenantId}`, "inactive");
    await act(view, "Activate", `Journey ${run}`);
    await expectStatus(`/admin/tenants/${tenantId}`, "active");
    await closeView(view);

    // Delete, in the order dependencies allow: the Subscription frees the Source's declarations and
    // the Destination, and the Source frees the Topic. Each is gone from reads once deleted.
    const gone = async (path: string) =>
      expect
        .poll(async () => (await fetch(`${adminOrigin}${path}`, { headers: { authorization: operatorKey! } })).status, {
          timeout: 15_000,
        })
        .toBe(404);
    for (const [screen, name, path] of [
      [`/tenants/${tenantId}/subscriptions/${topicId}/${subscriptionId}`, `${run}-to-sink`, subscriptionPath],
      [`/tenants/${tenantId}/sources/${sourceId}`, `${run}-intake`, sourcePath],
      [`/tenants/${tenantId}/destinations/${destinationId}`, `${run}-sink`, destinationPath],
      [`/tenants/${tenantId}/topics/${topicId}`, `${run}-orders`, `/admin/tenants/${tenantId}/topics/${topicId}`],
    ]) {
      view = await openDashboard(screen);
      await act(view, "Delete", name);
      await gone(path);
      await closeView(view);
    }
  }, 240_000);
});
