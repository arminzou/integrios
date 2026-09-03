// @vitest-environment node
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { chromium, type Browser, type Page, type Request } from "playwright";
import { createServer, type ViteDevServer } from "vite";

/// Every create form, filled and submitted through a real browser.
///
/// The jsdom tests dispatch React's synthetic events directly, and hand-driving the page through a
/// devtools protocol sets `.value` without React ever seeing it — neither exercises what a person
/// typing into the form actually produces. Playwright's fill and selectOption do, which is why the
/// request these assertions inspect is the request the API would really receive.
///
/// What they check is the part the type system cannot: the coercions between a form, which holds
/// only strings, and a JSON body with numbers, objects, and meaningful nulls.
const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const connectionId = "33333333-3333-3333-3333-333333333333";
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
const tenant = { id: tenantId, slug: "acme", name: "Acme", status: "active", environment: null, description: null, ...stamps };
const topic = { id: topicId, tenant_id: tenantId, name: "orders", status: "active", description: null, ...stamps };
const connection = { id: connectionId, tenant_id: tenantId, connector_id: connectorId, name: "sink", status: "active", environment: null, description: null, ...stamps };
const connector = { id: connectorId, key: "http", contract_version: 1, name: "HTTP", direction: "both", status: "active", description: null, ...stamps };

const page = (items: unknown[]) => ({ items, next_cursor: null });

const subscriptionId = "77777777-7777-7777-7777-777777777777";
const sourceId = "88888888-8888-8888-8888-888888888888";

const connectionDetail = { ...connection, config: { base_uri: "http://sink.invalid" }, source_verification: null, destination_authentication: null };
const subscriptionDetail = {
  id: subscriptionId,
  topic_id: topicId,
  tenant_id: tenantId,
  name: "to-sink",
  match_rules: { event_type: "order.created" },
  destination_connection_id: connectionId,
  mapping_config: null,
  http_delivery: { version: 1, method: "POST", path: null, headers: {}, body: "json" },
  status: "active",
  order_index: 1,
  description: null,
  ...stamps,
};
const sourceDetail = {
  id: sourceId,
  tenant_id: tenantId,
  connection_id: connectionId,
  topic_id: topicId,
  type: "event_api",
  configuration: { source_contract: "event_json" },
  status: "active",
  revoked_at: null,
  ...stamps,
};

/// One handler for every screen: reads answer from the path, writes are captured and accepted.
/// Per-endpoint stubs would be a fixture per screen for no extra coverage. Detail routes are
/// matched before their lists, because a list path is a prefix of the detail path under it.
function readFor(pathname: string): unknown {
  if (pathname === "/admin/connectors") return page([connector]);
  if (pathname === "/admin/tenants") return page([tenant]);
  if (/^\/admin\/tenants\/[^/]+$/.test(pathname)) return tenant;
  if (/\/connections\/[^/]+$/.test(pathname)) return connectionDetail;
  if (/\/subscriptions\/[^/]+$/.test(pathname)) return subscriptionDetail;
  if (/\/sources\/[^/]+$/.test(pathname)) return sourceDetail;
  if (/\/topics\/[^/]+$/.test(pathname)) return topic;
  if (/\/connections$/.test(pathname)) return page([connection]);
  if (/\/topics$/.test(pathname)) return page([topic]);
  if (/\/subscriptions$/.test(pathname)) return page([subscriptionDetail]);
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
    if (request.method() === "GET")
      return route.fulfill({ json: readFor(new URL(request.url()).pathname) });
    writes.push(request);
    return route.fulfill({ status: 201, json: { id: "66666666-6666-6666-6666-666666666666", ...stamps } });
  });

  await browserPage.goto(`${origin}${path}`);
  await browserPage.getByRole("heading", { level: 1 }).waitFor();
  return { page: browserPage, writes };
}

async function submitted(writes: Request[]): Promise<{ method: string; pathname: string; body: Record<string, unknown> }> {
  const request = writes[0];
  expect(request, "The form submitted no request at all.").toBeDefined();
  return {
    method: request.method(),
    pathname: new URL(request.url()).pathname,
    body: request.postDataJSON() as Record<string, unknown>,
  };
}

describe("Create forms, filled through a real browser", () => {
  it("sends a Connection with its config parsed out of the textarea", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/connections`);

    await view.selectOption("#create-connection-connector", connectorId);
    await view.fill("#create-connection-name", "sink");
    await view.fill("#create-connection-config", '{"base_uri":"http://sink.invalid"}');
    await view.click("text=Create Connection");
    await view.waitForFunction(() => true);

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/connections`);
    expect(sent.body.connector_id).toBe(connectorId);
    // The textarea holds text; the API takes a document.
    expect(sent.body.config).toEqual({ base_uri: "http://sink.invalid" });
    // Never claims to replace a scheme it did not round-trip.
    expect(sent.body.source_verification).toBeNull();
    expect(sent.body.destination_authentication).toBeNull();
    await view.close();
  }, 60_000);

  it("sends a Topic, leaving an untouched optional field null rather than empty", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/topics`);

    await view.fill("#create-topic-name", "orders");
    await view.click("text=Create Topic");

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/topics`);
    expect(sent.body.name).toBe("orders");
    expect(sent.body.description).toBeNull();
    await view.close();
  }, 60_000);

  it("sends a Subscription with a numeric order, an object delivery, and a null mapping", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/topics/${topicId}`);

    await view.fill("#create-subscription-name", "to-sink");
    await view.selectOption("#create-subscription-destination", connectionId);
    await view.fill("#create-subscription-order", "3");
    await view.fill("#create-subscription-match-rules", '{"all":[]}');
    await view.click("text=Create Subscription");

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/topics/${topicId}/subscriptions`);
    // A number input still yields a string; the API takes an int32.
    expect(sent.body.order_index).toBe(3);
    expect(sent.body.match_rules).toEqual({ all: [] });
    // An empty mapping is a real choice: deliver the Event unmapped.
    expect(sent.body.mapping).toBeNull();
    expect(sent.body.http_delivery).toMatchObject({ version: 1, method: "POST", body: "json", headers: {} });
    await view.close();
  }, 60_000);

  it("sends a Source with its type and configuration", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources`);

    await view.selectOption("#create-source-connection", connectionId);
    await view.selectOption("#create-source-topic", topicId);
    await view.selectOption("#create-source-type", "webhook");
    await view.fill("#create-source-configuration", '{"path":"/hook"}');
    await view.click("text=Create Source");

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/sources`);
    expect(sent.body.connection_id).toBe(connectionId);
    expect(sent.body.topic_id).toBe(topicId);
    expect(sent.body.type).toBe("webhook");
    expect(sent.body.configuration).toEqual({ path: "/hook" });
    await view.close();
  }, 60_000);
});

describe("Update and deactivate, driven through a real browser", () => {
  // Tenant, Topic and Source updates send only plain strings and are covered by the jsdom suite.
  // What is exercised here is what a string-only form has to convert: a parsed configuration
  // document and a numeric order index, plus the two shapes with no other coverage at all — a
  // confirmed deactivate, and the one DELETE the dashboard issues.

  it("sends an updated Connection with its config reparsed and its schemes untouched", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/connections/${connectionId}`);

    await view.fill("#connection-config", '{"base_uri":"http://moved.invalid"}');
    await view.click("text=Save changes");

    const sent = await submitted(writes);
    expect(sent.method).toBe("PATCH");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/connections/${connectionId}`);
    expect(sent.body.config).toEqual({ base_uri: "http://moved.invalid" });
    // The form never round-trips a scheme's secret references, so it must not claim to replace them.
    expect(sent.body.source_verification).toBeNull();
    expect(sent.body.destination_authentication).toBeNull();
    await view.close();
  }, 60_000);

  it("sends an updated Subscription with a numeric order and a mapping that stays null", async () => {
    const { page: view, writes } = await open(
      `/tenants/${tenantId}/topics/${topicId}/subscriptions/${subscriptionId}`,
    );

    await view.fill("#subscription-order", "7");
    await view.click("text=Save changes");

    const sent = await submitted(writes);
    expect(sent.method).toBe("PATCH");
    expect(sent.body.order_index).toBe(7);
    // The stored Subscription has no mapping; editing another field must not invent one.
    expect(sent.body.mapping).toBeNull();
    expect(sent.body.match_rules).toEqual({ event_type: "order.created" });
    await view.close();
  }, 60_000);

  it("deactivates a Topic only after the confirmation naming it", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/topics/${topicId}`);

    await view.click("text=Deactivate Topic");
    await view.getByText(/Deactivate the Topic "orders"\?/).waitFor();
    // Arming the confirmation must not be the action itself.
    expect(writes, "Deactivation ran before it was confirmed.").toHaveLength(0);

    await view.click("text=Deactivate orders");

    const sent = await submitted(writes);
    expect(sent.method).toBe("POST");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/topics/${topicId}/deactivate`);
    await view.close();
  }, 60_000);

  it("revokes a Source with the one DELETE the dashboard issues", async () => {
    const { page: view, writes } = await open(`/tenants/${tenantId}/sources/${sourceId}`);

    await view.click("text=Revoke Source");
    expect(writes, "Revocation ran before it was confirmed.").toHaveLength(0);
    await view.click(`text=Revoke ${sourceId}`);

    const sent = await submitted(writes);
    expect(sent.method).toBe("DELETE");
    expect(sent.pathname).toBe(`/admin/tenants/${tenantId}/sources/${sourceId}`);
    await view.close();
  }, 60_000);
});
