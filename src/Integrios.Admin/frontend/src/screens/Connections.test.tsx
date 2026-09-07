import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { type Call, page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { ConnectionsScreen } from "./Connections";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const connectorId = "22222222-2222-2222-2222-222222222222";

const connector = {
  id: connectorId,
  key: "http",
  contract_version: 1,
  name: "HTTP",
  direction: "both",
  status: "active",
  description: null,
  created_at: "2026-09-01T00:00:00Z",
  updated_at: "2026-09-01T00:00:00Z",
};

const writes = (calls: Call[]) => calls.filter((call) => call.method !== "GET");

/// The authoring pattern's two decisions that nothing else proves: what the form does with a
/// rejected write, and what it refuses to send at all.
async function openCreateForm(respond: (call: Call) => { status: number; body?: unknown }) {
  const calls = stubHttp(respond);
  renderScreen(<ConnectionsScreen tenantId={tenantId} />, `/tenants/${tenantId}/connections`);

  await screen.findByRole("heading", { level: 1, name: "Connections" });
  fireEvent.click(screen.getByText("New Connection"));

  // The list carries a Connector filter and a Connector column of its own, so the create form's own
  // controls are reached through the form rather than through the whole document. The Connector
  // picker is not touched here: it is a menu, and opening one needs a layout engine, so what it
  // takes to submit a valid Connection is proven in the browser layer instead.
  const form = within(await screen.findByRole("form", { name: "Create a Connection" }));
  fireEvent.change(form.getByLabelText("Name"), { target: { value: "sink" } });
  return calls;
}

const describedText = (control: HTMLElement) =>
  (control.getAttribute("aria-describedby") ?? "")
    .split(" ")
    .map((id) => document.getElementById(id)?.textContent ?? "")
    .join(" ");

describe("Creating a Connection", () => {
  it("never sends a configuration that is not well-formed JSON", async () => {
    const calls = await openCreateForm((call) => ({
      status: 200,
      body: page(call.url.pathname.endsWith("/connectors") ? [connector] : []),
    }));

    fireEvent.change(screen.getByLabelText("Configuration (JSON)"), { target: { value: "{not json" } });
    fireEvent.submit(screen.getByRole("button", { name: "Create Connection" }).closest("form")!);

    const config = screen.getByLabelText("Configuration (JSON)");
    await waitFor(() => expect(config.getAttribute("aria-invalid")).toBe("true"));
    // The message is the parser's own, not a generic "invalid" the Operator cannot act on.
    expect(describedText(config)).toContain("JSON");
    // The message on this field is what proves the rule fired; nothing is sent either way.
    expect(writes(calls)).toEqual([]);
  });

  it("creates from a sheet that is announced as a dialog and closes on Escape", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectionsScreen tenantId={tenantId} />);

    const trigger = await screen.findByRole("button", { name: "New Connection" });
    // A trigger that opens a dialog says so, and says whether it is open, before it is pressed.
    expect(trigger.getAttribute("aria-haspopup")).toBe("dialog");
    expect(trigger.getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(trigger);

    const sheet = await screen.findByRole("dialog", { name: "New Connection" });
    expect(trigger.getAttribute("aria-expanded")).toBe("true");
    // The form is inside the dialog rather than in the page behind it.
    expect(sheet.querySelector("form")).toBeTruthy();

    fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

const connectionId = "44444444-4444-4444-4444-444444444444";

const connection = {
  id: connectionId,
  tenant_id: tenantId,
  connector_id: connectorId,
  name: "northwind-erp",
  status: "active",
  environment: "production",
  description: "Order and payment records into the ERP.",
  config: { base_uri: "http://erp.internal/hooks" },
  source_verification: null,
  destination_authentication: null,
  created_at: "2026-09-01T00:00:00Z",
  updated_at: "2026-09-01T00:00:00Z",
};

describe("Connection selection", () => {
  it("reads the selected Connection beside the list it was chosen from", async () => {
    stubHttp(({ url }) => {
      if (url.pathname.endsWith("/connections")) return { status: 200, body: page([connection]) };
      if (url.pathname.endsWith(connectionId)) return { status: 200, body: connection };
      if (url.pathname.endsWith("/connectors")) return { status: 200, body: page([connector]) };
      return { status: 200, body: page([]) };
    });

    renderScreen(
      <ConnectionsScreen tenantId={tenantId} selectedConnectionId={connectionId} />,
      `/tenants/${tenantId}/connections/${connectionId}`,
    );

    // The list is still there to compare against, and the detail is a region of its own.
    await screen.findByRole("heading", { level: 1, name: "Connections" });
    const panel = await screen.findByRole("complementary", { name: "Connection detail" });
    expect(within(panel).getByRole("heading", { name: "northwind-erp" })).toBeTruthy();

    // The route is the selection, so the row it came from says so.
    const row = screen.getByRole("link", { name: "northwind-erp" });
    expect(row.getAttribute("aria-current")).toBe("page");

    // The destructive action names what it will change, and stays in the panel that shows it.
    expect(within(panel).getByRole("button", { name: /Deactivate/ })).toBeTruthy();
  });
});
