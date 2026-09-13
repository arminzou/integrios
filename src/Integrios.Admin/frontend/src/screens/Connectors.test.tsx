import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
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

const composedManifest = { composed_by: "admin" };

const listOnly = ({ method, url }: Call) => {
  if (method === "POST" && url.pathname.endsWith("/compose"))
    return { status: 200, body: { manifest: composedManifest } };
  return method === "PUT" ? { status: 201, body: installed } : { status: 200, body: page([]) };
};

/// The action appears with the list it belongs to, so this waits for it rather than assuming the
/// page header has one before the read has answered.
async function openAuthoring() {
  fireEvent.click(await screen.findByRole("button", { name: "New Connector" }));
}

async function openImport() {
  fireEvent.click(await screen.findByRole("button", { name: "Import manifest" }));
}

function fillBasics(key = "github", version?: string) {
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "GitHub" } });
  fireEvent.change(screen.getByLabelText("Key"), { target: { value: key } });
  if (version) fireEvent.change(screen.getByLabelText("Contract version"), { target: { value: version } });
}

const applied = (calls: Call[]) => calls.find((call) => call.method === "PUT");

async function applyDraft(label = "Create Connector") {
  const button = screen.getByRole("button", { name: label });
  await waitFor(() => expect(button.hasAttribute("disabled")).toBe(false));
  fireEvent.click(button);
}

describe("Opening a Connector from its row", () => {
  const listed = { status: 200, body: page([installed]) };

  it("opens from a cell that is not the link, because the whole row is the target", async () => {
    stubHttp(() => listed);

    const { router } = renderScreen(<ConnectorsScreen />);
    const name = await screen.findByText(installed.name);

    fireEvent.click(name);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/connectors/${installed.id}`));
  });

  it("leaves a click that ended a text selection alone, because copying an identifier is not opening it", async () => {
    stubHttp(() => listed);
    // What the browser reports mid-drag. An Operator lifting the pointer after selecting a key is
    // finishing a copy, and a row that navigated then would take the page out from under them.
    vi.spyOn(window, "getSelection").mockReturnValue({ toString: () => installed.key } as Selection);

    const { router } = renderScreen(<ConnectorsScreen />, "/connectors");
    const name = await screen.findByText(installed.name);

    fireEvent.click(name);

    expect(router.state.location.pathname).toBe("/connectors");
  });

  it("leaves a modified click to the browser, which is what opens it in a new tab", async () => {
    stubHttp(() => listed);

    const { router } = renderScreen(<ConnectorsScreen />, "/connectors");
    const name = await screen.findByText(installed.name);

    fireEvent.click(name, { ctrlKey: true });

    expect(router.state.location.pathname).toBe("/connectors");
  });
});

describe("A deployment with no Connectors", () => {
  it("offers no detail column to select into, because there is nothing to select", async () => {
    stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    await screen.findByRole("heading", { name: "No Connectors yet" });

    expect(screen.queryByText(/Select a Connector/)).toBeNull();
    expect(screen.queryByRole("complementary", { name: "Connector detail" })).toBeNull();
  });
});

describe("Authoring the first Connector", () => {
  it("applies the exact manifest Admin composed from the guided fields", async () => {
    // Bootstrap installs no Connectors, so this is the state of every fresh deployment. Without a
    // form here there is no Connector detail page to reach, and no way in from the browser at all.
    const calls = stubHttp(listOnly);

    const { router } = renderScreen(<ConnectorsScreen />);
    await screen.findByRole("heading", { name: "No Connectors yet" });

    await openAuthoring();
    fillBasics("github", "2");
    await applyDraft();

    await waitFor(() => expect(applied(calls)).toBeDefined());

    const compose = calls.find((call) => call.method === "POST" && call.url.pathname.endsWith("/compose"))!;
    expect(compose.url.pathname).toBe("/admin/connectors/github/versions/2/compose");
    expect(compose.body).toEqual({ name: "GitHub", description: null, direction: "source" });

    // The route identity stays in the guided fields. The PUT body is opaque to the dashboard and
    // is exactly the document returned by composition.
    const install = applied(calls)!;
    expect(install.url.pathname).toBe("/admin/connectors/github/versions/2");
    expect(install.body).toEqual(composedManifest);
    await waitFor(() => expect(router.state.location.pathname).toBe(`/connectors/${installed.id}`));
  });

  it("reads a key off the name until an Operator writes one of their own", async () => {
    const calls = stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    await openAuthoring();
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

    await applyDraft();
    await waitFor(() => expect(applied(calls)).toBeDefined());
    expect(applied(calls)!.url.pathname).toBe("/admin/connectors/field_service/versions/1");
  });

  it("refuses a draft with no capability rather than applying one the API would reject", async () => {
    const calls = stubHttp(listOnly);

    renderScreen(<ConnectorsScreen />);
    await openAuthoring();
    fillBasics();
    fireEvent.click(screen.getByRole("checkbox", { name: /Permit Sources/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));

    await screen.findByText(/Choose at least one\./);
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
  });

  it("reports a rejected manifest on the field the server named instead of appearing to install one", async () => {
    stubHttp(({ method, url }) => {
      if (method === "POST" && url.pathname.endsWith("/compose"))
        return { status: 200, body: { manifest: composedManifest } };
      return method === "PUT"
        ? { status: 422, body: { errors: { key: ["A Connector key must be lowercase."] } } }
        : { status: 200, body: page([]) };
    });

    renderScreen(<ConnectorsScreen />);
    await openAuthoring();
    fillBasics("GITHUB");
    await applyDraft();

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
  const importedManifest = {
    manifest_schema_version: 1,
    key: "slack",
    contract_version: 3,
    direction: "both",
    authored_extension: { preserved: true },
  };

  it("is a separate action that never seeds the guided form", async () => {
    stubHttp(listOnly);
    renderScreen(<ConnectorsScreen />);
    await screen.findByRole("heading", { name: "No Connectors yet" });

    await openImport();
    fireEvent.change(screen.getByLabelText("Connector manifest"), {
      target: { value: JSON.stringify(importedManifest) },
    });
    expect(screen.queryByLabelText("Name")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Close Import manifest" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Import manifest" })).toBeNull());

    await openAuthoring();
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("");
    expect((screen.getByLabelText("Key") as HTMLInputElement).value).toBe("");
  });

  it("keeps Apply disabled with a visible reason until key and version are present", async () => {
    stubHttp(listOnly);
    renderScreen(<ConnectorsScreen />);
    await openImport();
    const apply = screen.getByRole("button", { name: "Apply manifest" });

    expect(apply.hasAttribute("disabled")).toBe(true);
    expect(screen.getByText(/Paste a manifest to identify/)).toBeDefined();
    fireEvent.change(screen.getByLabelText("Connector manifest"), { target: { value: "{" } });
    expect(screen.getByText(/Expected property name|JSON/)).toBeDefined();
    expect(apply.hasAttribute("disabled")).toBe(true);

    fireEvent.change(screen.getByLabelText("Connector manifest"), { target: { value: "{}" } });
    expect(screen.getByText("The manifest must carry a non-empty string key.")).toBeDefined();
    expect(screen.getByText("Manifest schema version: not detected.")).toBeDefined();
    expect(screen.getByRole("link", { name: "Connector manifest reference" }).getAttribute("href")).toContain(
      "docs/connector-manifest.md",
    );
  });

  it.each(["Created", "Unchanged", "PresentationReconciled"])(
    "applies the pasted document unchanged and reports %s",
    async (outcome) => {
      const calls = stubHttp(({ method }) =>
        method === "PUT"
          ? {
              status: outcome === "Created" ? 201 : 200,
              body: { ...installed, key: "slack", contract_version: 3 },
              headers: { "X-Integrios-Connector-Manifest-Outcome": outcome },
            }
          : { status: 200, body: page([]) },
      );
      renderScreen(<ConnectorsScreen />);
      await openImport();
      fireEvent.change(screen.getByLabelText("Connector manifest"), {
        target: { value: JSON.stringify(importedManifest) },
      });

      expect(screen.getByText("Ready to apply slack contract v3.")).toBeDefined();
      expect(screen.getByText("Manifest schema version: 1.")).toBeDefined();
      fireEvent.click(screen.getByRole("button", { name: "Apply manifest" }));

      await screen.findByText(`${outcome} — slack contract v3.`);
      const request = applied(calls)!;
      expect(request.url.pathname).toBe("/admin/connectors/slack/versions/3");
      expect(request.body).toEqual(importedManifest);
    },
  );

  it("keeps one submitted manifest standing until its apply finishes", async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const calls = stubHttp(async ({ method }) => {
      if (method !== "PUT") return { status: 200, body: page([]) };
      await held;
      return {
        status: 201,
        body: { ...installed, key: "slack", contract_version: 3 },
        headers: { "X-Integrios-Connector-Manifest-Outcome": "Created" },
      };
    });
    renderScreen(<ConnectorsScreen />);
    await openImport();
    const textarea = screen.getByLabelText("Connector manifest") as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: JSON.stringify(importedManifest) } });
    fireEvent.click(screen.getByRole("button", { name: "Apply manifest" }));

    const pending = await screen.findByRole("button", { name: "Applying manifest…" });
    expect(textarea.disabled).toBe(true);
    fireEvent.change(textarea, { target: { value: JSON.stringify({ ...importedManifest, contract_version: 4 }) } });
    fireEvent.click(pending);
    expect(textarea.value).toBe(JSON.stringify(importedManifest));
    expect(calls.filter(({ method }) => method === "PUT")).toHaveLength(1);

    release();
    expect(await screen.findByText("Created — slack contract v3.")).toBeDefined();
  });

  it("refreshes a selected Connector after presentation reconciliation", async () => {
    const reconciled = {
      ...installed,
      name: "GitHub webhooks",
      description: "Updated presentation.",
      manifest: { ...importedManifest, key: installed.key, contract_version: installed.contract_version },
    };
    let applied = false;
    stubHttp(({ method, url }) => {
      if (method === "PUT") {
        applied = true;
        return {
          status: 200,
          body: reconciled,
          headers: { "X-Integrios-Connector-Manifest-Outcome": "PresentationReconciled" },
        };
      }
      if (url.pathname === `/admin/connectors/${installed.id}`)
        return { status: 200, body: applied ? reconciled : installed };
      return { status: 200, body: page([applied ? reconciled : installed]) };
    });
    renderScreen(<ConnectorsScreen selectedConnectorId={installed.id} />, `/connectors/${installed.id}`);
    expect(await screen.findByText(installed.name, { selector: "h2" })).toBeDefined();
    await openImport();
    fireEvent.change(screen.getByLabelText("Connector manifest"), {
      target: {
        value: JSON.stringify({
          ...importedManifest,
          key: installed.key,
          contract_version: installed.contract_version,
        }),
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply manifest" }));

    expect(await screen.findByText(reconciled.name, { selector: "h2" })).toBeDefined();
    expect(screen.getByText(reconciled.description)).toBeDefined();
  });

  it("renders a manifest-path rejection as an unassigned form error", async () => {
    stubHttp(({ method }) =>
      method === "PUT"
        ? {
            status: 422,
            body: { errors: { "source_verification.schemes": ["The scheme is not registered."] } },
          }
        : { status: 200, body: page([]) },
    );
    renderScreen(<ConnectorsScreen />);
    await openImport();
    fireEvent.change(screen.getByLabelText("Connector manifest"), {
      target: { value: JSON.stringify(importedManifest) },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply manifest" }));

    expect(await screen.findByText("The scheme is not registered.")).toBeDefined();
    expect((screen.getByLabelText("Connector manifest") as HTMLTextAreaElement).value).toContain('"key":"slack"');
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
      source_configuration_schema: {
        type: "object",
        properties: { organization: { type: "string" }, region: { type: "string" } },
        required: ["organization", "region"],
        additionalProperties: false,
      },
      source_verification: {
        allow_unverified: false,
        schemes: [{ scheme: "acme_signature", required_config: [], required_secret_refs: ["secret"] }],
      },
      destination_authentication: { allow_unauthenticated: true, schemes: [] },
      presentation: { name: "GitHub", description: "Webhooks.", event_types: ["github.push"] },
    },
  };

  const detail = ({ method, url }: Call) => {
    if (method === "POST" && url.pathname.endsWith("/compose"))
      return { status: 200, body: { manifest: composedManifest } };
    if (method === "PUT") return { status: 201, body: { ...github, id: "44444444-4444-4444-4444-444444444444" } };
    if (url.pathname === `/admin/connectors/${github.id}`) return { status: 200, body: github };
    return { status: 200, body: page([github]) };
  };

  it("explains capability menus and required configuration without showing JSON", async () => {
    stubHttp(detail);

    renderScreen(<ConnectorsScreen selectedConnectorId={github.id} />, `/connectors/${github.id}`);

    expect(await screen.findByText("acme_signature")).toBeDefined();
    expect(screen.getByText("Selection required.")).toBeDefined();
    expect(screen.getByText("organization, region")).toBeDefined();
    expect(screen.getByText("Destinations on this Connector cannot authenticate Deliveries.")).toBeDefined();
    expect(screen.queryByText(/"source_verification"/)).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Show raw JSON" }));
    expect(screen.getByText(/"source_verification"/)).toBeDefined();
    expect(screen.getByRole("button", { name: "Hide raw JSON" }).getAttribute("aria-expanded")).toBe("true");
  });

  it("explains what an empty Source verification menu forecloses", async () => {
    const noVerification = {
      ...github,
      manifest: {
        ...github.manifest,
        source_verification: { allow_unverified: true, schemes: [] },
      },
    };
    stubHttp(({ url }) =>
      url.pathname === `/admin/connectors/${github.id}`
        ? { status: 200, body: noVerification }
        : { status: 200, body: page([noVerification]) },
    );

    renderScreen(<ConnectorsScreen selectedConnectorId={github.id} />, `/connectors/${github.id}`);

    expect(await screen.findByText("Sources on this Connector cannot verify Events.")).toBeDefined();
    expect(screen.getAllByText("Selection optional.").length).toBe(2);
  });

  it("falls back to raw JSON for an unknown manifest schema", async () => {
    const future = { ...github, manifest_schema_version: 2, manifest: { future_contract: true } };
    stubHttp(({ url }) =>
      url.pathname === `/admin/connectors/${github.id}`
        ? { status: 200, body: future }
        : { status: 200, body: page([future]) },
    );

    renderScreen(<ConnectorsScreen selectedConnectorId={github.id} />, `/connectors/${github.id}`);

    expect(
      await screen.findByText("This dashboard does not explain manifest schema version 2. Review the raw JSON."),
    ).toBeDefined();
    expect(screen.getByText(/"future_contract"/)).toBeDefined();
    expect(screen.queryByText("Source verification")).toBeNull();
  });

  it("seeds the guided fields from the applied Connector and applies Admin's next-version composition", async () => {
    const calls = stubHttp(detail);

    renderScreen(<ConnectorsScreen selectedConnectorId={github.id} />, `/connectors/${github.id}`);
    fireEvent.click(await screen.findByRole("button", { name: "Create new version" }));

    // The copy opens on the next version, and the key is identity rather than something to retype.
    const key = (await screen.findByLabelText("Key")) as HTMLInputElement;
    expect(key.value).toBe("github");
    expect(key.readOnly).toBe(true);
    expect((screen.getByLabelText("Contract version") as HTMLInputElement).value).toBe("3");
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe(github.name);
    expect((screen.getByLabelText("Description") as HTMLTextAreaElement).value).toBe(github.description ?? "");

    await applyDraft("Create version");
    await waitFor(() => expect(applied(calls)).toBeDefined());

    const compose = calls.find((call) => call.method === "POST" && call.url.pathname.endsWith("/compose"))!;
    expect(compose.url.pathname).toBe("/admin/connectors/github/versions/3/compose");
    const install = applied(calls)!;
    expect(install.url.pathname).toBe("/admin/connectors/github/versions/3");
    expect(install.body).toEqual(composedManifest);
  });

  it("cannot be edited in place by applying the version it already has", async () => {
    const calls = stubHttp(detail);

    renderScreen(<ConnectorsScreen selectedConnectorId={github.id} />, `/connectors/${github.id}`);
    fireEvent.click(await screen.findByRole("button", { name: "Create new version" }));
    fireEvent.change(await screen.findByLabelText("Contract version"), { target: { value: "2" } });
    await applyDraft("Create version");

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
