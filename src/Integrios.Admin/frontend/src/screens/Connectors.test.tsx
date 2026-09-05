import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { ConnectorsScreen } from "./Connectors";

afterEach(cleanup);

const installed = {
  id: "99999999-9999-9999-9999-999999999999",
  key: "http",
  contract_version: 1,
  manifest_schema_version: 1,
  name: "HTTP",
  direction: "destination",
  status: "active",
  description: null,
  manifest: { key: "http" },
  created_at: "2026-09-01T00:00:00Z",
  updated_at: "2026-09-01T00:00:00Z",
};

describe("Installing the first Connector", () => {
  it("can be done from the list, which is the only screen a deployment with none can reach", async () => {
    // Bootstrap installs no Connectors, so this is the state of every fresh deployment. Without a
    // form here there is no Connector detail page to reach, and no way in from the browser at all.
    const calls = stubHttp(({ method }) =>
      method === "PUT" ? { status: 201, body: installed } : { status: 200, body: page([]) },
    );

    render(<ConnectorsScreen />);
    await screen.findByText("No Connectors match this filter.");

    fireEvent.change(screen.getByLabelText("Key"), { target: { value: "http" } });
    fireEvent.change(screen.getByLabelText("Contract version"), { target: { value: "2" } });
    fireEvent.change(screen.getByLabelText("Manifest (JSON)"), { target: { value: '{"key":"http"}' } });
    fireEvent.click(screen.getByRole("button", { name: "Install Connector" }));

    await waitFor(() => expect(calls.some((call) => call.method === "PUT")).toBe(true));

    // The key the Operator typed selects the Connector, and the version selects which contract of
    // it is being installed.
    const install = calls.find((call) => call.method === "PUT")!;
    expect(install.url.pathname).toBe("/admin/connectors/http/versions/2");
    expect(install.body).toEqual({ key: "http" });
  });

  it("reports a rejected manifest instead of appearing to install one", async () => {
    stubHttp(({ method }) =>
      method === "PUT"
        ? { status: 422, body: { errors: { key: ["A Connector key must be lowercase."] } } }
        : { status: 200, body: page([]) },
    );

    render(<ConnectorsScreen />);
    const key = await screen.findByLabelText("Key");
    fireEvent.change(key, { target: { value: "HTTP" } });
    fireEvent.change(screen.getByLabelText("Manifest (JSON)"), { target: { value: "{}" } });
    fireEvent.click(screen.getByRole("button", { name: "Install Connector" }));

    const message = await screen.findByText("A Connector key must be lowercase.");
    expect(key.getAttribute("aria-describedby")?.split(" ")).toEqual(["install-connector-key-hint", message.id]);
    expect((key as HTMLInputElement).value).toBe("HTTP");
  });

  it("refuses a manifest that is not JSON before it reaches the server", async () => {
    const calls = stubHttp(() => ({ status: 200, body: page([]) }));

    render(<ConnectorsScreen />);
    fireEvent.change(await screen.findByLabelText("Key"), { target: { value: "http" } });
    fireEvent.change(screen.getByLabelText("Manifest (JSON)"), { target: { value: "{not json" } });
    fireEvent.click(screen.getByRole("button", { name: "Install Connector" }));

    await screen.findByRole("alert");
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
  });
});
