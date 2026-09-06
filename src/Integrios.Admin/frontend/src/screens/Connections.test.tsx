import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
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
  await screen.findByRole("option", { name: /HTTP/ });

  fireEvent.change(screen.getByLabelText("Connector"), { target: { value: connectorId } });
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "sink" } });
  return calls;
}

const describedText = (control: HTMLElement) =>
  (control.getAttribute("aria-describedby") ?? "")
    .split(" ")
    .map((id) => document.getElementById(id)?.textContent ?? "")
    .join(" ");

describe("Creating a Connection", () => {
  it("puts each rejected field beside its own control and everything else at form level", async () => {
    await openCreateForm((call) =>
      call.method === "GET"
        ? { status: 200, body: page(call.url.pathname.endsWith("/connectors") ? [connector] : []) }
        : {
            status: 400,
            body: {
              title: "The Connection was rejected.",
              // The server names fields in its own casing, and attributes what belongs to no
              // rendered field to none at all.
              errors: { Name: ["A Connection called sink already exists."], "": ["The Tenant is not active."] },
            },
          },
    );

    fireEvent.submit(screen.getByRole("button", { name: "Create Connection" }).closest("form")!);

    const name = await screen.findByLabelText("Name");
    await waitFor(() => expect(name.getAttribute("aria-invalid")).toBe("true"));
    expect(describedText(name)).toContain("A Connection called sink already exists.");

    // Everything the server did not attribute to a rendered field is still reported.
    expect(
      screen
        .getAllByRole("alert")
        .map((alert) => alert.textContent ?? "")
        .join(" "),
    ).toContain("The Tenant is not active.");
    // The field's own message is beside its control and is not repeated at form level.
    expect(screen.getAllByText("A Connection called sink already exists.")).toHaveLength(1);
  });

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
    expect(writes(calls)).toEqual([]);
  });

  it("opens the create panel from the page header, and says what the action controls", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectionsScreen tenantId={tenantId} />);

    const trigger = await screen.findByRole("button", { name: "New Connection" });
    expect(trigger.getAttribute("aria-expanded")).toBe("false");
    // The panel it names is in the document even while closed, so the relationship always resolves.
    const panelId = trigger.getAttribute("aria-controls");
    expect(panelId).toBe("new-connection");
    expect(document.getElementById(panelId as string)?.hasAttribute("hidden")).toBe(true);

    fireEvent.click(trigger);
    expect(trigger.getAttribute("aria-expanded")).toBe("true");
    expect(document.getElementById(panelId as string)?.hasAttribute("hidden")).toBe(false);
  });
});
