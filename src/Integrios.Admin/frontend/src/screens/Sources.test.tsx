import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { SourcesScreen } from "./Sources";
import { guidedExpression } from "./sourceMapping";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const connectorId = "44444444-4444-4444-4444-444444444444";
const sourceId = "55555555-5555-5555-5555-555555555555";

it("hides Source input requirements and restarts paging when the Topic filter changes", async () => {
  const calls = stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return {
        status: 200,
        body: page([{ id: topicId, key: "orders", name: "orders" }], "more-topics"),
      };
    if (!url.pathname.endsWith("/sources")) return { status: 200, body: page([]) };
    const second = url.searchParams.has("after");
    return {
      status: 200,
      body: page(
        [
          {
            id: second ? "source-2" : "source-1",
            name: second ? "Invoices intake" : "Orders intake",
            tenant_id: tenantId,
            topic_id: topicId,
            connector_id: connectorId,
            type: "event_api",
            status: "active",
            input_requirements: second ? "second_requirements" : "order_requirements",
          },
        ],
        "cursor-1",
      ),
    };
  });
  const { router } = renderScreen(<SourcesScreen tenantId={tenantId} />, `/tenants/${tenantId}/sources`);
  await screen.findByRole("link", { name: "Orders intake" });
  expect(screen.getByRole("link", { name: "Orders intake" }).getAttribute("href")).toBe(
    `/tenants/${tenantId}/sources/source-1`,
  );
  expect(screen.queryByRole("link", { name: "source-1" })).toBeNull();
  expect(screen.queryByRole("columnheader", { name: "Input requirements" })).toBeNull();
  expect(screen.queryByText("order_requirements")).toBeNull();
  const topicFilter = screen.getByLabelText("Topic");
  expect(topicFilter.getAttribute("aria-describedby")).toBe("source-topic-hint");
  expect(document.getElementById("source-topic-hint")?.textContent).toBe("Showing the first 100 Topics.");
  fireEvent.click(screen.getByRole("button", { name: "Load more" }));
  await screen.findByRole("link", { name: "Invoices intake" });
  await act(() => router.navigate(`/tenants/${tenantId}/sources?topic_id=${topicId}`));
  await waitFor(() => {
    const latest = calls.filter(({ url }) => url.pathname.endsWith("/sources")).at(-1)!;
    expect(latest.url.searchParams.get("topic_id")).toBe(topicId);
    expect(latest.url.searchParams.has("after")).toBe(false);
  });
  expect(screen.queryByRole("link", { name: "Invoices intake" })).toBeNull();
  expect(screen.getByLabelText("Topic").textContent).toContain("orders");
  expect(screen.getByRole("button", { name: "Clear filters" })).toBeTruthy();
});

/// `broker` is the wire value for the type an Operator authors as "Message broker". The filter and
/// the authoring control read the label; a row that printed the wire value would name the same type
/// differently one control apart, and would describe a topic subscription as a queue.
it("names a broker Source's type as the Operator authored it", async () => {
  stubHttp(({ url }) => {
    if (!url.pathname.endsWith("/sources")) return { status: 200, body: page([]) };
    return {
      status: 200,
      body: page([
        {
          id: sourceId,
          name: "Orders broker",
          tenant_id: tenantId,
          topic_id: topicId,
          connector_id: connectorId,
          type: "broker",
          status: "active",
          input_requirements: null,
        },
      ]),
    };
  });
  renderScreen(<SourcesScreen tenantId={tenantId} />, `/tenants/${tenantId}/sources`);
  const row = await screen.findByRole("row", { name: /Orders broker/ });
  expect(within(row).getByText("Message broker")).toBeTruthy();
  expect(within(row).queryByText("broker")).toBeNull();
});

it("requires an explicit Source type", async () => {
  stubHttp(({ url }) => {
    if (url.pathname.endsWith("/connectors"))
      return { status: 200, body: page([{ id: connectorId, key: "http", name: "HTTP", status: "active" }]) };
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, key: "orders", name: "Orders", status: "active" }]) };
    return { status: 200, body: page([]) };
  });

  renderScreen(<SourcesScreen tenantId={tenantId} />, `/tenants/${tenantId}/sources`);
  fireEvent.click(await screen.findByRole("button", { name: "New Source" }));

  expect(screen.getByLabelText("Type").textContent).toContain("Choose a type");
  expect(screen.queryByText(/callback URL/)).toBeNull();
  expect(screen.queryByRole("heading", { name: "Event Normalization" })).toBeNull();
});

describe("Authoring a Source before anything it needs", () => {
  it("says a Topic has to exist first, instead of offering an empty picker", async () => {
    // A Tenant authored a moment ago: a Connector is installed deployment-wide, and nothing else.
    stubHttp(({ url }) =>
      url.pathname.endsWith("/connectors")
        ? { status: 200, body: page([{ id: connectorId, key: "http", name: "HTTP", status: "active" }]) }
        : { status: 200, body: page([]) },
    );

    renderScreen(<SourcesScreen tenantId={tenantId} />, `/tenants/${tenantId}/sources`);
    fireEvent.click(await screen.findByRole("button", { name: "New Source" }));

    const hint = await screen.findByText(/No Topics yet/);
    expect(hint.textContent).toContain("a Source needs one");
    expect(within(hint).getByRole("link", { name: "Create a Topic" }).getAttribute("href")).toBe(
      `/tenants/${tenantId}/topics`,
    );

    // Neither the picker nor the write pretends to be available: there is nothing to choose, and
    // nothing this form could send.
    expect(screen.getByLabelText("Topic").hasAttribute("disabled")).toBe(true);
    expect(screen.getByRole("button", { name: "Create Source" }).hasAttribute("disabled")).toBe(true);
  });
});

describe("Source setup guide", () => {
  const cases = [
    {
      type: "event_api",
      configuration: {},
      heading: "Construct the Event request",
      fact: "Authorization: Bearer <TenantApiKey>",
    },
    {
      type: "webhook",
      configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
      heading: "Configure the provider callback",
      fact: "http://localhost:5231/webhooks/66666666-6666-6666-6666-666666666666",
    },
    {
      type: "broker",
      configuration: {
        transport: "azure_service_bus",
        authentication: { scheme: "azure_identity" },
        transport_config: { namespace: "acme.servicebus.windows.net", queue_name: "orders" },
      },
      heading: "Publish to the broker",
      fact: "acme.servicebus.windows.net/orders",
    },
  ] as const;

  it.each(cases)("shows Source-specific $type guidance", async ({ type, configuration, heading, fact }) => {
    guideHttp({ type, configuration });
    renderScreen(
      <SourcesScreen tenantId={tenantId} selectedSourceId={sourceId} />,
      `/tenants/${tenantId}/sources/${sourceId}`,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Open setup guide" }));
    expect(await screen.findByRole("heading", { name: heading })).toBeTruthy();
    expect(screen.getAllByText(fact, { exact: false }).length).toBeGreaterThan(0);
    if (type === "webhook")
      expect(screen.getByText("Required media type:").parentElement?.textContent).toContain("application/json");
    expect(screen.getByRole("list", { name: "Event path" }).textContent).toContain("Matching Subscriptions");
  });

  /// An Inactive Source is where setup happens: the publisher is configured from the guide before the
  /// Source is activated, so nothing in it is withheld, and the guide says why Events are refused.
  it("keeps the request copyable for an Inactive Source and says it refuses Events", async () => {
    const clipboard = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: clipboard } });
    guideHttp({ type: "event_api", configuration: {} });
    const active = renderScreen(
      <SourcesScreen tenantId={tenantId} selectedSourceId={sourceId} />,
      `/tenants/${tenantId}/sources/${sourceId}`,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Open setup guide" }));
    fireEvent.click(await screen.findByRole("button", { name: "Copy http request" }));
    await waitFor(() =>
      expect(clipboard).toHaveBeenCalledWith(expect.stringContaining(`/events?source_id=${sourceId}`)),
    );
    active.unmount();

    guideHttp({
      type: "event_api",
      configuration: {},
      status: "inactive",
    });
    renderScreen(
      <SourcesScreen tenantId={tenantId} selectedSourceId={sourceId} />,
      `/tenants/${tenantId}/sources/${sourceId}`,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Open setup guide" }));
    expect(await screen.findByText("This Source is Inactive.", { selector: "strong" })).toBeTruthy();
    expect(await screen.findByRole("button", { name: "Copy http request" })).toBeTruthy();
  });
});

it("opens Source creation from one-shot state with the filtered Topic selected", async () => {
  stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, key: "orders", name: "orders", status: "active" }]) };
    if (url.pathname.endsWith("/connectors"))
      return { status: 200, body: page([{ id: connectorId, name: "input", status: "active" }]) };
    return { status: 200, body: page([]) };
  });

  renderScreen(<SourcesScreen tenantId={tenantId} />, {
    pathname: `/tenants/${tenantId}/sources`,
    search: `?topic_id=${topicId}`,
    state: { openSourceCreate: true },
  });

  const dialog = await screen.findByRole("dialog", { name: "New Source" });
  await waitFor(() => expect(within(dialog).getByLabelText("Topic").textContent).toContain("orders"));
});

/// An untouched guided verification selection round-trips its full stored document. Dropping the
/// config or a second secret reference while editing something else would silently change how the
/// webhook is verified.
it("keeps a webhook Source's verification when its mapping is edited", async () => {
  const verification = { scheme: "hmac_sha256", config: { header: "X-Signature" }, secret_refs: { secret: "gh-hook" } };
  const calls = stubHttp(({ method, url }) => {
    if (method === "PUT") return { status: 200, body: {} };
    if (url.pathname.endsWith(`/sources/${sourceId}`))
      return {
        status: 200,
        body: {
          id: sourceId,
          tenant_id: tenantId,
          connector_id: connectorId,
          topic_id: topicId,
          name: "webhook-intake",
          type: "webhook",
          configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
          verification,
          input_requirements: null,
          mapping: null,
          event_identity_rule: null,
          event_types: ["order.created"],
          revision: "revision",
          status: "active",
          created_at: "2026-09-09T00:00:00Z",
          updated_at: "2026-09-09T00:00:00Z",
        },
      };
    return { status: 200, body: page([]) };
  });
  renderScreen(
    <SourcesScreen tenantId={tenantId} selectedSourceId={sourceId} />,
    `/tenants/${tenantId}/sources/${sourceId}`,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  const form = await screen.findByRole("form", { name: "Edit Webhook Source" });
  expect(within(form).queryByRole("heading", { name: "Raw broker configuration" })).toBeNull();
  fireEvent.click(within(form).getByText("Raw event contract"));
  fireEvent.change(within(form).getByLabelText("Event mapping (JSONata, optional)"), { target: { value: "payload" } });

  // A changed mapping retypes this Source's Events, so Enter alone does not save it.
  await act(async () => {
    fireEvent.submit(form);
  });
  expect(calls.some((call) => call.method === "PUT")).toBe(false);

  fireEvent.click(within(form).getByRole("button", { name: "Save configuration" }));
  const confirm = await screen.findByRole("dialog", { name: "Save configuration" });
  await within(confirm).findByText(/No Active Subscription on this Topic depends on it yet/);
  expect(within(confirm).getByText(/Events already accepted keep their Event type/)).toBeTruthy();
  fireEvent.click(within(confirm).getByRole("button", { name: "Save configuration" }));

  await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));
  expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({
    configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
    verification,
    mapping: { engine: "jsonata", version: "1", expression: "payload" },
  });
});

/// Edits a webhook Source's secret reference, then each labelled setting in `settings`, and returns
/// the verification the Save sent. A null manifest makes the Connector request fail;
/// `manifestAfterOpen` holds it until the form is open.
async function saveSecretReference(
  verification: { scheme: string; config: object; secret_refs: Record<string, string> },
  manifest: object | null,
  expectedShown: string,
  manifestAfterOpen = false,
  settings: Record<string, string> = {},
) {
  let openForm = () => {};
  const formOpen = manifestAfterOpen ? new Promise<void>((resolve) => (openForm = resolve)) : Promise.resolve();
  const calls = stubHttp(async ({ method, url }) => {
    if (method === "PUT") return { status: 200, body: {} };
    if (url.pathname.endsWith(`/connectors/${connectorId}`)) {
      await formOpen;
      return manifest
        ? {
            status: 200,
            body: {
              id: connectorId,
              key: "webhooks",
              contract_version: 1,
              manifest_schema_version: 1,
              name: "Webhooks",
              direction: "source",
              status: "active",
              manifest,
              created_at: "2026-09-09T00:00:00Z",
              updated_at: "2026-09-09T00:00:00Z",
            },
          }
        : { status: 500, body: {} };
    }
    if (url.pathname.endsWith(`/sources/${sourceId}`))
      return {
        status: 200,
        body: {
          id: sourceId,
          tenant_id: tenantId,
          connector_id: connectorId,
          topic_id: topicId,
          name: "webhook-intake",
          type: "webhook",
          configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
          verification,
          input_requirements: null,
          mapping: null,
          event_identity_rule: null,
          event_types: ["order.created"],
          revision: "revision",
          status: "active",
          created_at: "2026-09-09T00:00:00Z",
          updated_at: "2026-09-09T00:00:00Z",
        },
      };
    return { status: 200, body: page([]) };
  });
  renderScreen(
    <SourcesScreen tenantId={tenantId} selectedSourceId={sourceId} />,
    `/tenants/${tenantId}/sources/${sourceId}`,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  const form = await screen.findByRole("form", { name: "Edit Webhook Source" });
  openForm();
  await waitFor(() =>
    expect(calls.some((call) => call.url.pathname.endsWith(`/connectors/${connectorId}`))).toBe(true),
  );
  const secretReference = within(form).getByLabelText("Secret reference") as HTMLInputElement;
  await waitFor(() => expect(secretReference.value).toBe(expectedShown));
  fireEvent.change(secretReference, { target: { value: "rotated" } });
  for (const [label, value] of Object.entries(settings))
    fireEvent.change(within(form).getByLabelText(label), { target: { value } });
  await act(async () => {
    fireEvent.submit(form);
  });

  await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));
  return (calls.find((call) => call.method === "PUT")!.body as { verification: unknown }).verification;
}

const verificationManifest = (scheme: string, secretField: string) => ({
  source_verification: {
    allow_unverified: false,
    schemes: [{ scheme, required_config: [], required_secret_refs: [secretField] }],
  },
});

it("saves a verification secret reference under the field its scheme declares", async () => {
  const verification = { scheme: "header_token", config: {}, secret_refs: { token: "old" } };

  expect(await saveSecretReference(verification, verificationManifest("header_token", "token"), "old")).toEqual({
    ...verification,
    secret_refs: { token: "rotated" },
  });
});

/// A Save while the Connector request has failed must not invent a "secret" field: intake would keep
/// checking the old token while the form reported the rotation saved.
it("keeps a Source's stored secret field when its Connector manifest is unavailable", async () => {
  const verification = { scheme: "header_token", config: {}, secret_refs: { token: "old" } };

  expect(await saveSecretReference(verification, null, "old")).toEqual({
    ...verification,
    secret_refs: { token: "rotated" },
  });
});

it("edits the reference its scheme uses when a Source stores several", async () => {
  const verification = { scheme: "header_token", config: {}, secret_refs: { secret: "extra", token: "current" } };

  expect(
    await saveSecretReference(verification, verificationManifest("header_token", "token"), "current", true),
  ).toEqual({
    ...verification,
    secret_refs: { secret: "extra", token: "rotated" },
  });
});

it("keeps a stored verification setting and saves an edited one", async () => {
  const verification = {
    scheme: "header_token",
    config: { header_name: "X-Gitlab-Token" },
    secret_refs: { token: "old" },
  };

  expect(
    await saveSecretReference(verification, verificationManifest("header_token", "token"), "old", false, {
      Prefix: "Bearer ",
    }),
  ).toEqual({
    scheme: "header_token",
    config: { header_name: "X-Gitlab-Token", prefix: "Bearer " },
    secret_refs: { token: "rotated" },
  });
});

/// Shopify signs without the "sha256=" prefix, so an empty prefix has to be saved, not read as "use
/// the default".
it("saves an HMAC prefix cleared to empty", async () => {
  const verification = { scheme: "hmac_sha256", config: {}, secret_refs: { secret: "old" } };

  expect(
    await saveSecretReference(verification, verificationManifest("hmac_sha256", "secret"), "old", false, {
      Header: "X-Shopify-Hmac-Sha256",
      Prefix: "",
    }),
  ).toEqual({
    scheme: "hmac_sha256",
    config: { header_name: "X-Shopify-Hmac-Sha256", prefix: "" },
    secret_refs: { secret: "rotated" },
  });
});

it("falls back to the default header when the Header field is cleared", async () => {
  const verification = {
    scheme: "header_token",
    config: { header_name: "X-Gitlab-Token" },
    secret_refs: { token: "old" },
  };

  expect(
    await saveSecretReference(verification, verificationManifest("header_token", "token"), "old", false, {
      Header: "",
    }),
  ).toEqual({ scheme: "header_token", config: {}, secret_refs: { token: "rotated" } });
});

/// Intake reads any case of "base64" as base64, so the picker must show such a stored value, not a blank.
it.each([
  ["BASE64", "base64"],
  ["", "hex"],
])("reads a stored HMAC encoding %j the way intake does", async (stored, saved) => {
  const verification = { scheme: "hmac_sha256", config: { encoding: stored }, secret_refs: { secret: "old" } };

  expect(await saveSecretReference(verification, verificationManifest("hmac_sha256", "secret"), "old")).toEqual({
    scheme: "hmac_sha256",
    config: { encoding: saved },
    secret_refs: { secret: "rotated" },
  });
});

/// Closing Edit without saving is Cancel. A draft that outlived it came back on the next Edit reading
/// as the Source's configuration - and the Event Builder took it for what the Source had.
it("discards an unsaved Source draft when the Edit sheet closes", async () => {
  const stored = guidedExpression({ source: "header", header: "x-github-event", prefix: "github" });
  const calls = stubHttp(({ method, url }) => {
    if (url.pathname.endsWith(`/sources/${sourceId}`))
      return {
        status: 200,
        body: {
          id: sourceId,
          tenant_id: tenantId,
          connector_id: connectorId,
          topic_id: topicId,
          name: "github-intake",
          type: "webhook",
          configuration: { callback_id: "66666666-6666-6666-6666-666666666666" },
          verification: null,
          input_requirements: null,
          mapping: { engine: "jsonata", version: "1", expression: stored },
          event_identity_rule: { kind: "header", value: "x-github-delivery", allow_missing: false },
          event_types: ["github.push"],
          revision: "revision",
          status: "active",
          created_at: "2026-09-09T00:00:00Z",
          updated_at: "2026-09-09T00:00:00Z",
        },
      };
    return { status: method === "GET" ? 200 : 500, body: page([]) };
  });
  renderScreen(
    <SourcesScreen tenantId={tenantId} selectedSourceId={sourceId} />,
    `/tenants/${tenantId}/sources/${sourceId}`,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  let form = await screen.findByRole("form", { name: "Edit Webhook Source" });
  expect(within(form).queryByText(/Not saved yet/)).toBeNull();
  fireEvent.click(within(form).getByText("Raw event contract"));
  fireEvent.change(within(form).getByLabelText("Event mapping (JSONata, optional)"), {
    target: { value: '{ "event_type": repository.owner.email, "payload": $ }' },
  });
  // While the sheet is open the summary shows the draft, and says it is one.
  expect(within(form).getByText(/Not saved yet/)).toBeTruthy();

  fireEvent.click(screen.getByRole("button", { name: "Close Edit" }));
  await waitFor(() => expect(screen.queryByRole("form", { name: "Edit Webhook Source" })).toBeNull());

  fireEvent.click(screen.getByRole("button", { name: "Edit" }));
  form = await screen.findByRole("form", { name: "Edit Webhook Source" });
  expect(within(form).queryByText(/Not saved yet/)).toBeNull();
  // The saved rule, as a template: fixed text, then the part read from each request.
  expect(within(form).getByText("github.")).toBeTruthy();
  expect(within(form).getByText("x-github-event")).toBeTruthy();
  fireEvent.click(within(form).getByText("Raw event contract"));
  expect((within(form).getByLabelText("Event mapping (JSONata, optional)") as HTMLTextAreaElement).value).toBe(stored);
  expect(calls.some((call) => call.method === "PUT")).toBe(false);
});

function guideHttp({
  type,
  configuration,
  status = "active",
}: {
  type: string;
  configuration: Record<string, unknown>;
  status?: string;
}) {
  stubHttp(({ url }) => {
    const path = url.pathname;
    if (path.endsWith(`/sources/${sourceId}`))
      return {
        status: 200,
        body: {
          id: sourceId,
          tenant_id: tenantId,
          connector_id: connectorId,
          topic_id: topicId,
          name: "orders-intake",
          type,
          configuration,
          verification: type === "webhook" ? { scheme: "hmac_sha256", config: {} } : null,
          input_requirements: {},
          mapping: null,
          event_identity_rule: null,
          event_types: ["order.created"],
          revision: "revision",
          status,
          created_at: "2026-09-09T00:00:00Z",
          updated_at: "2026-09-09T00:00:00Z",
        },
      };
    if (path.endsWith(`/connectors/${connectorId}`))
      return {
        status: 200,
        body: {
          id: connectorId,
          key: type === "webhook" ? "github" : type === "broker" ? "dataverse" : "http",
          contract_version: 1,
          manifest_schema_version: 1,
          name: type === "webhook" ? "GitHub" : type === "broker" ? "Dataverse" : "HTTP",
          direction: "source",
          status: "active",
          manifest: {},
          created_at: "2026-09-09T00:00:00Z",
          updated_at: "2026-09-09T00:00:00Z",
        },
      };
    if (path.endsWith(`/topics/${topicId}`))
      return {
        status: 200,
        body: {
          id: topicId,
          tenant_id: tenantId,
          key: "orders",
          name: "orders",
          status: "active",
          description: null,
          subscription_count: 1,
          created_at: "2026-09-09T00:00:00Z",
          updated_at: "2026-09-09T00:00:00Z",
        },
      };
    if (path.endsWith("/overview"))
      return {
        status: 200,
        body: {
          topics: 1,
          destinations: 1,
          sources: 1,
          subscriptions: 1,
          live_api_keys: 1,
          ingestion_endpoint: "http://localhost:5231/",
        },
      };
    return { status: 200, body: page([]) };
  });
}
