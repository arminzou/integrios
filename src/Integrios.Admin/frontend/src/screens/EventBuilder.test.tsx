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

const sampleBody = JSON.stringify({ action: "opened", repository: { full_name: "northwind/orders" } });

const applied = (calls: Call[]) => calls.find((call) => call.method === "PUT");
const previewed = (calls: Call[]) => calls.find((call) => call.url.pathname.endsWith("/source-contracts/preview"));

/// Opens the Builder on a provider-native draft with one representative header and body, which is
/// the state every guided choice is made from.
async function openBuilder() {
  fireEvent.click(screen.getByRole("button", { name: "New Connector" }));
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "GitHub" } });
  fireEvent.change(screen.getByLabelText("Key"), { target: { value: "github" } });
  fireEvent.click(screen.getByRole("radio", { name: /Provider-native JSON/ }));
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));

  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });
  fireEvent.change(within(builder).getByLabelText("Header 1 name"), { target: { value: "X-GitHub-Event" } });
  fireEvent.change(within(builder).getByLabelText("Header 1 representative value"), { target: { value: "issues" } });
  fireEvent.change(within(builder).getByLabelText("Request body (JSON)"), { target: { value: sampleBody } });
  fireEvent.change(within(builder).getByLabelText("Text prefix"), { target: { value: "github" } });
  return builder;
}

describe("Building a Source contract from a representative request", () => {
  it("maps the Event from discovered headers and fields, and hands only the expression back", async () => {
    const calls = stubHttp(({ method }) =>
      method === "PUT" ? { status: 201, body: installed } : { status: 200, body: page([]) },
    );

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();

    // The header is normalized the way the runtime lower-cases it, and the body's own paths appear
    // once it parses — neither needs an Analyze press.
    const eventHeader = await within(builder).findByLabelText("Event name from header");
    await waitFor(() => expect(within(eventHeader as HTMLElement).getByText("x-github-event")).toBeDefined());
    fireEvent.change(eventHeader, { target: { value: "x-github-event" } });
    fireEvent.change(within(builder).getByLabelText("Append field"), { target: { value: "action" } });

    fireEvent.click(within(builder).getByRole("button", { name: "Use configuration" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Integrios Event Builder" })).toBeNull());

    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));
    await waitFor(() => expect(applied(calls)).toBeDefined());

    const contract = (applied(calls)!.body as { source_contracts: Record<string, unknown>[] }).source_contracts[0];
    expect(contract.mapping).toEqual({
      engine: "jsonata",
      version: "1",
      expression: expect.stringContaining("$context.headers.`x-github-event`") as unknown as string,
    });
    // The guided choices are not a second model beside the expression: only the expression is sent.
    expect(JSON.stringify(contract)).not.toContain("eventPrefix");
    expect(contract).not.toHaveProperty("schema");
  });

  it("keeps the last valid fields while a body edit is invalid, and says they are stale", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    await waitFor(() =>
      expect(within(within(builder).getByLabelText("Append field") as HTMLElement).getByText("action")).toBeDefined(),
    );

    fireEvent.change(within(builder).getByLabelText("Request body (JSON)"), { target: { value: "{not json" } });

    await within(builder).findByText(/Showing fields from the last valid request body/);
    // The choices made from the last valid body survive the broken edit rather than emptying out.
    expect(within(within(builder).getByLabelText("Append field") as HTMLElement).getByText("action")).toBeDefined();
  });

  it("declares input requirements as a schema, empty and collapsed until one is added", async () => {
    const calls = stubHttp(({ method }) =>
      method === "PUT" ? { status: 201, body: installed } : { status: 200, body: page([]) },
    );

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();

    const disclosure = within(builder)
      .getByText(/Input requirements \(optional\)/)
      .closest("details")!;
    expect(disclosure.open).toBe(false);
    expect(within(builder).queryByLabelText("Required field 1")).toBeNull();

    fireEvent.click(await within(builder).findByRole("button", { name: "Add required field" }));
    fireEvent.change(within(builder).getByLabelText("Required field 1"), { target: { value: "action" } });
    // Nothing preselects the type from the sample; until one is chosen the draft cannot be used.
    expect((within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
    fireEvent.change(within(builder).getByLabelText("Required field 1 type"), { target: { value: "string" } });

    fireEvent.click(within(builder).getByRole("button", { name: "Use configuration" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Integrios Event Builder" })).toBeNull());
    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));
    await waitFor(() => expect(applied(calls)).toBeDefined());

    const contract = (applied(calls)!.body as { source_contracts: Record<string, unknown>[] }).source_contracts[0];
    expect(contract.schema).toEqual({
      type: "object",
      properties: { action: { type: "string" } },
      required: ["action"],
      additionalProperties: true,
    });
  });

  it("refuses a requirement the representative body contradicts", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();

    fireEvent.click(await within(builder).findByRole("button", { name: "Add required field" }));
    fireEvent.change(within(builder).getByLabelText("Required field 1"), { target: { value: "action" } });
    fireEvent.change(within(builder).getByLabelText("Required field 1 type"), { target: { value: "integer" } });

    await within(builder).findByText("action is not integer in this request body.");
    expect((within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });
});

describe("Previewing the normalized Event", () => {
  it("sends the contract, the sample and the bounded context to the stateless endpoint", async () => {
    const calls = stubHttp(({ url }) =>
      url.pathname.endsWith("/source-contracts/preview")
        ? { status: 200, body: { output: { event_type: "github.issues", payload: {} } } }
        : { status: 200, body: page([]) },
    );

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    const eventHeader = await within(builder).findByLabelText("Event name from header");
    await waitFor(() => expect(within(eventHeader as HTMLElement).getByText("x-github-event")).toBeDefined());
    fireEvent.change(eventHeader, { target: { value: "x-github-event" } });
    // The body is analysed on its own after a pause, so the sample the preview sends is only ready
    // once its fields have appeared.
    await waitFor(() =>
      expect(within(within(builder).getByLabelText("Append field") as HTMLElement).getByText("action")).toBeDefined(),
    );

    fireEvent.click(within(builder).getByRole("button", { name: "Preview normalized Event" }));
    await waitFor(() => expect(previewed(calls)).toBeDefined());

    expect(previewed(calls)!.body).toEqual({
      schema: null,
      mapping: { engine: "jsonata", version: "1", expression: expect.any(String) as unknown as string },
      sample_input: JSON.parse(sampleBody),
      sample_context: { headers: { "x-github-event": "issues" } },
    });
    await within(builder).findByText("Would be accepted");
    // Preview is evidence, not a gate, and it stores nothing: the Connector is still appliable.
    expect((within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement).disabled).toBe(
      false,
    );
  });

  it("marks a result out of date when the sample it was produced from changes", async () => {
    stubHttp(({ url }) =>
      url.pathname.endsWith("/source-contracts/preview")
        ? { status: 200, body: { output: { event_type: "github", payload: {} } } }
        : { status: 200, body: page([]) },
    );

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    fireEvent.click(within(builder).getByRole("button", { name: "Preview normalized Event" }));
    await within(builder).findByText("Would be accepted");

    // A header the mapping can read is part of what produced this result, so editing one dates it.
    fireEvent.change(within(builder).getByLabelText("Header 1 representative value"), { target: { value: "push" } });
    await within(builder).findByText("Result is out of date");
  });

  it("names the stage that refused it rather than reporting one failure for the whole pipeline", async () => {
    stubHttp(({ url }) =>
      url.pathname.endsWith("/source-contracts/preview")
        ? {
            status: 400,
            body: {
              title: "One or more validation errors occurred.",
              errors: { "": ["schema.properties.action.type 'array' is not supported."] },
            },
          }
        : { status: 200, body: page([]) },
    );

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    fireEvent.click(within(builder).getByRole("button", { name: "Preview normalized Event" }));

    // The stage is the document the API named, not the pane the result is shown in.
    await within(builder).findByText("Input requirements");
    await within(builder).findByText(/type 'array' is not supported/);
  });
});

describe("Advanced JSONata", () => {
  it("returns to the guided form freely, and only destructively once the expression diverges", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();

    fireEvent.click(within(builder).getByRole("button", { name: "Advanced JSONata" }));
    // A generated expression is representable, so going back is not a decision worth confirming.
    fireEvent.click(await within(builder).findByRole("button", { name: "Guided mode" }));
    await within(builder).findByRole("button", { name: "Advanced JSONata" });

    fireEvent.click(within(builder).getByRole("button", { name: "Advanced JSONata" }));
    const editor = await within(builder).findByLabelText("Source mapping expression");
    fireEvent.change(editor, { target: { value: '{ "event_type": $lowercase(kind), "payload": $ }' } });

    fireEvent.click(await within(builder).findByRole("button", { name: "Reset to guided" }));
    const confirm = await screen.findByRole("dialog", { name: "Reset to guided" });
    expect(confirm.textContent).toContain("cannot be shown in the guided form");
    fireEvent.click(within(confirm).getByRole("button", { name: "Reset to guided" }));

    await within(builder).findByRole("button", { name: "Advanced JSONata" });
  });

  it("completes only what the representative request and the runtime evaluator really carry", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    fireEvent.click(within(builder).getByRole("button", { name: "Advanced JSONata" }));

    const editor = await within(builder).findByLabelText("Source mapping expression");
    fireEvent.change(editor, { target: { value: "$context.headers.x-git" } });

    const suggestions = await within(builder).findByRole("list", { name: "JSONata suggestions" });
    expect(within(suggestions).getByText("`x-github-event`")).toBeDefined();
  });
});

describe("Choices the representative request stops supporting", () => {
  it("keeps a chosen header visible, names it, and refuses to carry it into a Connector", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    const eventHeader = await within(builder).findByLabelText("Event name from header");
    await waitFor(() => expect(within(eventHeader as HTMLElement).getByText("x-github-event")).toBeDefined());
    fireEvent.change(eventHeader, { target: { value: "x-github-event" } });

    fireEvent.click(within(builder).getByRole("button", { name: "Remove header 1" }));

    // The mapping still reads that header, so the draft cannot be used while it addresses nothing.
    await within(builder).findByText("x-github-event (not in this request)");
    expect((within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it("keeps a requirement row removable after the body stops carrying any top-level value", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    fireEvent.click(await within(builder).findByRole("button", { name: "Add required field" }));
    fireEvent.change(within(builder).getByLabelText("Required field 1"), { target: { value: "action" } });
    fireEvent.change(within(builder).getByLabelText("Required field 1 type"), { target: { value: "string" } });

    fireEvent.change(within(builder).getByLabelText("Request body (JSON)"), {
      target: { value: JSON.stringify({ repository: { full_name: "x" } }) },
    });

    // The row outlives the field it named, so it can still be read and taken back out.
    const row = await within(builder).findByLabelText("Required field 1");
    expect((row as HTMLSelectElement).value).toBe("action");
    fireEvent.click(within(builder).getByRole("button", { name: "Remove required field 1" }));
    await waitFor(() =>
      expect((within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement).disabled).toBe(
        false,
      ),
    );
  });

  it("refuses a payload that names one field twice", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    fireEvent.click(within(builder).getByRole("radio", { name: "Choose payload fields" }));

    for (const source of ["action", "repository.full_name"]) {
      fireEvent.click(within(builder).getByRole("button", { name: "Add payload field" }));
      const index = source === "action" ? 1 : 2;
      fireEvent.change(within(builder).getByLabelText(`Payload field ${index} name`), { target: { value: "id" } });
      await waitFor(() =>
        expect(
          within(within(builder).getByLabelText(`Payload field ${index} source`) as HTMLElement).getByText(source),
        ).toBeDefined(),
      );
      fireEvent.change(within(builder).getByLabelText(`Payload field ${index} source`), { target: { value: source } });
    }

    await within(builder).findByText("Payload field id is named more than once.");
    expect((within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });
});

describe("The boundary between the Builder and the Connector draft", () => {
  it("resyncs an emptied editor with the guided mapping instead of handing back nothing", async () => {
    const calls = stubHttp(({ method }) =>
      method === "PUT" ? { status: 201, body: installed } : { status: 200, body: page([]) },
    );

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    fireEvent.click(within(builder).getByRole("button", { name: "Advanced JSONata" }));
    fireEvent.change(await within(builder).findByLabelText("Source mapping expression"), { target: { value: "" } });

    // An empty editor is representable, so returning is not destructive — but it still has to leave
    // the expression saying what the guided pane now claims it says.
    fireEvent.click(within(builder).getByRole("button", { name: "Guided mode" }));
    fireEvent.click(await within(builder).findByRole("button", { name: "Use configuration" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Integrios Event Builder" })).toBeNull());

    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));
    await waitFor(() => expect(applied(calls)).toBeDefined());
    const contract = (applied(calls)!.body as { source_contracts: Record<string, unknown>[] }).source_contracts[0];
    expect((contract.mapping as { expression: string }).expression).toBe('{ "event_type": "github", "payload": $ }');
  });

  it("does not attach a provider-native input schema to a normalized Event contract", async () => {
    const calls = stubHttp(({ method }) =>
      method === "PUT" ? { status: 201, body: installed } : { status: 200, body: page([]) },
    );

    renderScreen(<ConnectorsScreen />);
    const builder = await openBuilder();
    fireEvent.click(await within(builder).findByRole("button", { name: "Add required field" }));
    fireEvent.change(within(builder).getByLabelText("Required field 1"), { target: { value: "action" } });
    fireEvent.change(within(builder).getByLabelText("Required field 1 type"), { target: { value: "string" } });
    fireEvent.click(within(builder).getByRole("button", { name: "Use configuration" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Integrios Event Builder" })).toBeNull());

    // The requirements were authored against a provider's own request; a Publisher sending normalized
    // Event JSON would be rejected by them.
    fireEvent.click(screen.getByRole("radio", { name: /Integrios Event JSON/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create Connector" }));
    await waitFor(() => expect(applied(calls)).toBeDefined());

    const contract = (applied(calls)!.body as { source_contracts: Record<string, unknown>[] }).source_contracts[0];
    expect(contract.key).toBe("event_json");
    expect(contract).not.toHaveProperty("schema");
  });
});
