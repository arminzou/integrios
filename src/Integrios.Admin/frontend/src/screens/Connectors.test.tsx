import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { type Call, page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { ConnectorsScreen } from "./Connectors";

afterEach(cleanup);

const installed = {
  id: "99999999-9999-9999-9999-999999999999",
  key: "github",
  contract_version: 1,
  manifest_schema_version: 1,
  name: "GitHub",
  direction: "source",
  status: "active",
  description: null,
  manifest: { key: "github" },
  created_at: "2026-09-01T00:00:00Z",
  updated_at: "2026-09-01T00:00:00Z",
};

const listOnly = ({ method }: { method: string }) =>
  method === "PUT" ? { status: 201, body: installed } : { status: 200, body: page([]) };

function openAuthoring() {
  fireEvent.click(screen.getByRole("button", { name: "New Connector" }));
}

function fillBasics(key = "github", version?: string) {
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "GitHub" } });
  fireEvent.change(screen.getByLabelText("Key"), { target: { value: key } });
  if (version) fireEvent.change(screen.getByLabelText("Contract version"), { target: { value: version } });
}

const applied = (calls: Call[]) => calls.find((call) => call.method === "PUT");

describe("Authoring the first Connector", () => {
  it("produces a manifest from the guided form, without the Operator writing one", async () => {
    // Bootstrap installs no Connectors, so this is the state of every fresh deployment. Without a
    // form here there is no Connector detail page to reach, and no way in from the browser at all.
    const calls = stubHttp(listOnly);

    const { router } = renderScreen(<ConnectorsScreen />);
    await screen.findByText(/No Connectors are installed/);

    openAuthoring();
    fillBasics("github", "2");
    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));

    await waitFor(() => expect(applied(calls)).toBeDefined());

    // The key and the version select which immutable Connector contract is being installed, and the
    // body is the manifest the guided draft generated.
    const install = applied(calls)!;
    expect(install.url.pathname).toBe("/admin/connectors/github/versions/2");
    expect(install.body).toMatchObject({
      manifest_schema_version: 1,
      key: "github",
      contract_version: 2,
      direction: "source",
      source_verification: { allow_unverified: true, schemes: [] },
      presentation: { name: "GitHub", description: null },
    });
    await waitFor(() => expect(router.state.location.pathname).toBe(`/connectors/${installed.id}`));
  });

  it("reads a key off the name until an Operator writes one of their own", async () => {
    const calls = stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    openAuthoring();
    const key = screen.getByLabelText("Key") as HTMLInputElement;

    // Lower snake_case starting with a letter is what the Admin API accepts, so that is what a name
    // is read down to — the leading digits of "3M" cannot begin a key and are dropped.
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "GitHub" } });
    await waitFor(() => expect(key.value).toBe("github"));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "3M Field Service!" } });
    await waitFor(() => expect(key.value).toBe("m_field_service"));

    // Emptying the name empties the key with it. The form's dirty state turns over when a field
    // returns to its default, which once made the key look authored and froze it a letter short.
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "" } });
    await waitFor(() => expect(key.value).toBe(""));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Slack" } });
    await waitFor(() => expect(key.value).toBe("slack"));

    // Once the Operator writes a key, the name stops deciding it.
    fireEvent.change(key, { target: { value: "field_service" } });
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Something Else" } });
    await waitFor(() => expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("Something Else"));
    expect(key.value).toBe("field_service");

    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));
    await waitFor(() => expect(applied(calls)).toBeDefined());
    expect(applied(calls)!.url.pathname).toBe("/admin/connectors/field_service/versions/1");
  });

  it("follows the chosen capabilities into the manifest's direction and its configuration schemas", async () => {
    const calls = stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    openAuthoring();
    fillBasics("http");
    fireEvent.click(screen.getByRole("checkbox", { name: /Deliver Events over HTTP/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));

    await waitFor(() => expect(applied(calls)).toBeDefined());
    const body = applied(calls)!.body as Record<string, unknown>;
    expect(body.direction).toBe("both");
    expect(body).toHaveProperty("source_configuration_schema");
    expect(body).toHaveProperty("destination_configuration_schema");
  });

  it("emits only the selected capability and never Source-owned contracts", async () => {
    const calls = stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    openAuthoring();
    fillBasics("http");
    fireEvent.click(screen.getByRole("checkbox", { name: /Deliver Events over HTTP/ }));
    fireEvent.click(screen.getByRole("checkbox", { name: /Receive Events/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));

    await waitFor(() => expect(applied(calls)).toBeDefined());
    const body = applied(calls)!.body as Record<string, unknown>;
    expect(body.direction).toBe("destination");
    expect(body).not.toHaveProperty("source_configuration_schema");
    expect(body).not.toHaveProperty("source_contracts");
    expect(body).not.toHaveProperty("http_success");
  });

  it("refuses a draft with no capability rather than applying one the API would reject", async () => {
    const calls = stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    openAuthoring();
    fillBasics();
    fireEvent.click(screen.getByRole("checkbox", { name: /Receive Events/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));

    await screen.findByText("Choose at least one capability.");
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
  });

  it("reports a rejected manifest on the field the server named instead of appearing to install one", async () => {
    stubHttp(({ method }) =>
      method === "PUT"
        ? { status: 422, body: { errors: { key: ["A Connector key must be lowercase."] } } }
        : { status: 200, body: page([]) },
    );

    renderScreen(<ConnectorsScreen />);
    openAuthoring();
    fillBasics("GITHUB");
    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));

    const message = await screen.findByText("A Connector key must be lowercase.");
    const key = screen.getByLabelText("Key");
    const described = (key.getAttribute("aria-describedby") ?? "")
      .split(" ")
      .map((id) => document.getElementById(id)?.textContent ?? "")
      .join(" ");
    // The field's own hint travels with the message rather than being replaced by it.
    expect(described).toContain("Names this Connector");
    expect(described).toContain(message.textContent);
    // The draft is still standing, so the Operator corrects it rather than authoring it again.
    expect((key as HTMLInputElement).value).toBe("GITHUB");
  });
});

describe("Importing a Connector manifest", () => {
  const imported = {
    manifest_schema_version: 1,
    key: "slack",
    contract_version: 3,
    direction: "both",
    destination_configuration_schema: { type: "object", properties: { webhook_uri: { type: "string" } } },
    source_verification: { allow_unverified: false, schemes: [{ scheme: "slack_signature", required_config: [] }] },
    destination_authentication: {
      allow_unauthenticated: false,
      schemes: [{ scheme: "bearer_token", required_config: [], required_secret_refs: ["token"] }],
    },
    source_contracts: [{ key: "events_api", contract_version: 1 }],
    http_success: { kind: "status_code", expected: 201 },
    presentation: { name: "Slack", description: "Slack Events API.", event_types: ["slack.message"] },
  };

  function importDraft(document: unknown) {
    fireEvent.change(screen.getByLabelText("Manifest (JSON)"), { target: { value: JSON.stringify(document) } });
  }

  it("replaces the draft only after confirmation, and keeps what the guided form cannot show", async () => {
    const calls = stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    openAuthoring();
    fillBasics("github");
    importDraft(imported);

    // Confirmation first: an import discards whatever is already authored.
    fireEvent.click(screen.getByRole("button", { name: "Replace draft" }));
    const confirm = await screen.findByRole("dialog");
    expect(confirm.textContent).toContain("Everything authored in this form is discarded.");
    fireEvent.click(within(confirm).getByRole("button", { name: "Replace draft" }));

    await waitFor(() => expect((screen.getByLabelText("Key") as HTMLInputElement).value).toBe("slack"));
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("Slack");
    // What the form has no control for is named rather than quietly dropped.
    expect(screen.getByText(/Kept from the imported manifest/).textContent).toContain("presentation.event_types");

    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));
    await waitFor(() => expect(applied(calls)).toBeDefined());

    const body = applied(calls)!.body as Record<string, unknown>;
    expect(applied(calls)!.url.pathname).toBe("/admin/connectors/slack/versions/3");
    expect(body.destination_configuration_schema).toEqual(imported.destination_configuration_schema);
    expect(body.source_verification).toEqual(imported.source_verification);
    expect(body.destination_authentication).toEqual(imported.destination_authentication);
    expect(body).not.toHaveProperty("source_contracts");
    expect(body).not.toHaveProperty("http_success");
    expect(body.presentation).toMatchObject(imported.presentation);
  });

  it("reports invalid JSON without offering to replace the draft with it", async () => {
    stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    openAuthoring();
    fillBasics("github");
    fireEvent.change(screen.getByLabelText("Manifest (JSON)"), { target: { value: "{not json" } });

    await screen.findByRole("alert");
    expect(screen.queryByRole("button", { name: "Replace draft" })).toBeNull();
    expect((screen.getByLabelText("Key") as HTMLInputElement).value).toBe("github");
  });
});

describe("An applied Connector version", () => {
  const github = {
    ...installed,
    contract_version: 2,
    manifest: {
      manifest_schema_version: 1,
      key: "github",
      contract_version: 2,
      direction: "source",
      source_configuration_schema: { type: "object", properties: {}, additionalProperties: true },
      source_verification: { allow_unverified: false, schemes: [{ scheme: "acme_signature", required_config: [] }] },
      destination_authentication: { allow_unauthenticated: true, schemes: [] },
      presentation: { name: "GitHub", description: "Webhooks.", event_types: ["github.push"] },
    },
  };

  const detail = ({ method, url }: Call) => {
    if (method === "PUT") return { status: 201, body: { ...github, id: "44444444-4444-4444-4444-444444444444" } };
    if (url.pathname === `/admin/connectors/${github.id}`) return { status: 200, body: github };
    return { status: 200, body: page([github]) };
  };

  it("is copied into a later version, carrying values the guided form cannot show", async () => {
    const calls = stubHttp(detail);

    renderScreen(<ConnectorsScreen selectedConnectorId={github.id} />, `/connectors/${github.id}`);
    fireEvent.click(await screen.findByRole("button", { name: "Create new version" }));

    // The copy opens on the next version, and the key is identity rather than something to retype.
    const key = (await screen.findByLabelText("Key")) as HTMLInputElement;
    expect(key.value).toBe("github");
    expect(key.readOnly).toBe(true);
    expect((screen.getByLabelText("Contract version") as HTMLInputElement).value).toBe("3");

    fireEvent.click(screen.getByRole("button", { name: "Create version" }));
    await waitFor(() => expect(applied(calls)).toBeDefined());

    const install = applied(calls)!;
    expect(install.url.pathname).toBe("/admin/connectors/github/versions/3");
    const manifest = install.body as Record<string, unknown>;
    expect(manifest.contract_version).toBe(3);
    // Everything the form has no control for survives the copy rather than being rewritten out.
    expect(manifest.source_verification).toEqual(github.manifest.source_verification);
    expect(manifest.destination_authentication).toEqual(github.manifest.destination_authentication);
    expect(manifest.presentation).toMatchObject({ event_types: ["github.push"] });
  });

  it("cannot be edited in place by applying the version it already has", async () => {
    const calls = stubHttp(detail);

    renderScreen(<ConnectorsScreen selectedConnectorId={github.id} />, `/connectors/${github.id}`);
    fireEvent.click(await screen.findByRole("button", { name: "Create new version" }));
    fireEvent.change(await screen.findByLabelText("Contract version"), { target: { value: "2" } });
    fireEvent.click(screen.getByRole("button", { name: "Create version" }));

    await screen.findByText("Version 2 is applied. Choose a later version.");
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
  });

  it("keeps Source authoring outside the Connector version flow", async () => {
    stubHttp(detail);

    renderScreen(<ConnectorsScreen selectedConnectorId={github.id} />, `/connectors/${github.id}`);
    await screen.findByRole("heading", { level: 1, name: "Connectors" });

    // Source contracts and their builder belong to the concrete Source, not this reusable version.
    expect(screen.queryByText("Preview a Source contract")).toBeNull();
    fireEvent.click(await screen.findByRole("button", { name: "Create new version" }));
    expect(screen.queryByRole("button", { name: "Open Integrios Event Builder" })).toBeNull();
    expect(screen.getByText(/Source-specific verification, input requirements, mapping, and identity/)).toBeDefined();
  });
});
