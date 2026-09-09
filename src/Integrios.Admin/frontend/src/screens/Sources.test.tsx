import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { SourcesScreen } from "./Sources";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const connectionId = "33333333-3333-3333-3333-333333333333";
const connectorId = "44444444-4444-4444-4444-444444444444";
const sourceId = "55555555-5555-5555-5555-555555555555";

it("shows the Source contract and restarts paging when the Topic filter changes", async () => {
  const calls = stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, name: "orders", status: "disabled" }], "more-topics") };
    if (!url.pathname.endsWith("/sources")) return { status: 200, body: page([]) };
    const second = url.searchParams.has("after");
    return {
      status: 200,
      body: page(
        [
          {
            id: second ? "source-2" : "source-1",
            tenant_id: tenantId,
            topic_id: topicId,
            connection_id: "connection",
            type: "event_api",
            status: "active",
            source_contract: second ? "second_contract" : "order_json",
          },
        ],
        "cursor-1",
      ),
    };
  });
  const { router } = renderScreen(<SourcesScreen tenantId={tenantId} />, `/tenants/${tenantId}/sources`);
  await screen.findByText("order_json");
  expect(screen.getByRole("columnheader", { name: "Source contract" })).toBeTruthy();
  const topicFilter = screen.getByLabelText("Topic");
  expect(topicFilter.getAttribute("aria-describedby")).toBe("source-topic-hint");
  expect(document.getElementById("source-topic-hint")?.textContent).toBe("Showing the first 100 Topics.");
  fireEvent.click(screen.getByRole("button", { name: "Load more" }));
  await screen.findByText("second_contract");
  await act(() => router.navigate(`/tenants/${tenantId}/sources?topic_id=${topicId}`));
  await waitFor(() => {
    const latest = calls.filter(({ url }) => url.pathname.endsWith("/sources")).at(-1)!;
    expect(latest.url.searchParams.get("topic_id")).toBe(topicId);
    expect(latest.url.searchParams.has("after")).toBe(false);
  });
  expect(screen.queryByText("second_contract")).toBeNull();
  expect(screen.getByLabelText("Topic").textContent).toContain("orders");
  expect(screen.getByRole("link", { name: "Clear filters" })).toBeTruthy();
});

describe("Source setup guide", () => {
  const cases = [
    {
      type: "event_api",
      configuration: { source_contract: "event_json" },
      contract: "event_json",
      heading: "Construct the Event request",
      fact: "Authorization: TenantApiKey <tenant-api-key>",
    },
    {
      type: "webhook",
      configuration: { source_contract: "verified_webhook", callback_id: "66666666-6666-6666-6666-666666666666" },
      contract: "verified_webhook",
      heading: "Configure the provider callback",
      fact: "http://localhost:5231/webhooks/66666666-6666-6666-6666-666666666666",
    },
    {
      type: "queue",
      configuration: {
        source_contract: "remote_execution_context_json",
        transport: "azure_service_bus",
        authentication: { scheme: "azure_identity" },
        transport_config: { namespace: "acme.servicebus.windows.net", queue_name: "orders" },
      },
      contract: "remote_execution_context_json",
      heading: "Publish to the broker",
      fact: "acme.servicebus.windows.net/orders",
    },
  ] as const;

  it.each(cases)("shows contract-backed $type guidance", async ({ type, configuration, contract, heading, fact }) => {
    guideHttp({ type, configuration, contract });
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

  it("copies an active request and removes runnable copy actions for a revoked Source", async () => {
    const clipboard = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: clipboard } });
    guideHttp({ type: "event_api", configuration: { source_contract: "event_json" }, contract: "event_json" });
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
      configuration: { source_contract: "event_json" },
      contract: "event_json",
      status: "revoked",
    });
    renderScreen(
      <SourcesScreen tenantId={tenantId} selectedSourceId={sourceId} />,
      `/tenants/${tenantId}/sources/${sourceId}`,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Open setup guide" }));
    expect(await screen.findByText("This Source cannot accept new Events.", { selector: "strong" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^Copy / })).toBeNull();
    expect(screen.getByText("When this Source was active", { exact: false })).toBeTruthy();
    expect(screen.queryByText("Running copied material", { exact: false })).toBeNull();
  });
});

it("opens Source creation from one-shot state with the filtered Topic selected", async () => {
  stubHttp(({ url }) => {
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, name: "orders", status: "active" }]) };
    if (url.pathname.endsWith("/connections"))
      return { status: 200, body: page([{ id: connectionId, name: "input", status: "active" }]) };
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

function guideHttp({
  type,
  configuration,
  contract,
  status = "active",
}: {
  type: string;
  configuration: Record<string, unknown>;
  contract: string;
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
          connection_id: connectionId,
          topic_id: topicId,
          type,
          configuration,
          status,
          revoked_at: status === "revoked" ? "2026-09-09T00:00:00Z" : null,
          created_at: "2026-09-09T00:00:00Z",
          updated_at: "2026-09-09T00:00:00Z",
        },
      };
    if (path.endsWith(`/connections/${connectionId}`))
      return {
        status: 200,
        body: {
          id: connectionId,
          tenant_id: tenantId,
          connector_id: connectorId,
          name: "Acme input",
          config: {},
          source_verification: type === "webhook" ? { scheme: "hmac_sha256", config: {} } : null,
          destination_authentication: null,
          status: "active",
          created_at: "2026-09-09T00:00:00Z",
          updated_at: "2026-09-09T00:00:00Z",
        },
      };
    if (path.endsWith(`/connectors/${connectorId}`))
      return {
        status: 200,
        body: {
          id: connectorId,
          key: type === "webhook" ? "github" : type === "queue" ? "dataverse" : "http",
          contract_version: 1,
          manifest_schema_version: 1,
          name: type === "webhook" ? "GitHub" : type === "queue" ? "Dataverse" : "HTTP",
          direction: "source",
          status: "active",
          manifest: { source_contracts: [{ key: contract, contract_version: 1, config: {} }] },
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
          connections: 1,
          sources: 1,
          subscriptions: 1,
          live_api_keys: 1,
          dead_lettered_deliveries: 0,
          ingestion_endpoint: "http://localhost:5231/",
        },
      };
    return { status: 200, body: page([]) };
  });
}
