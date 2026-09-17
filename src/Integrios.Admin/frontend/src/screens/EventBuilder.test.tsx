import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { EventBuilder, type SourceContractDraft } from "./EventBuilder";
import { SourcesScreen } from "./Sources";
import { guidedExpression } from "./sourceMapping";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const connectorId = "33333333-3333-3333-3333-333333333333";

function stubOptions() {
  stubHttp(({ url }) => {
    if (url.pathname.endsWith("/connectors"))
      return { status: 200, body: page([{ id: connectorId, name: "GitHub", status: "active" }]) };
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, key: "orders", name: "orders", status: "active" }]) };
    return { status: 200, body: page([]) };
  });
}

async function openSource(type: "event_api" | "webhook" | "broker" = "webhook") {
  stubOptions();
  renderScreen(<SourcesScreen tenantId={tenantId} />);
  fireEvent.click(await screen.findByRole("button", { name: "New Source" }));
  const dialog = await screen.findByRole("dialog", { name: "New Source" });
  fireEvent.change(within(dialog).getByLabelText("Connector"), { target: { value: connectorId } });
  fireEvent.change(within(dialog).getByLabelText("Topic"), { target: { value: topicId } });
  const typeSelect = within(dialog).getByRole("combobox", { name: "Type" });
  fireEvent.change(typeSelect.nextElementSibling!, { target: { value: type } });
  return dialog;
}

it("explains webhook normalization and shows its sample request", async () => {
  const webhook = await openSource();
  expect(within(webhook).getByRole("heading", { name: "Webhook request" })).toBeTruthy();
  expect(within(webhook).getByRole("heading", { name: "Event Normalization" })).toBeTruthy();
  expect(within(webhook).getByRole("button", { name: "Open Integrios Event Builder" })).toBeTruthy();
  fireEvent.click(within(webhook).getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });
  expect(within(builder).getByRole("heading", { name: "Sample request" })).toBeTruthy();
  expect(within(builder).getByText("Request headers")).toBeTruthy();
  expect(within(builder).getByLabelText("Request body (JSON)")).toBeTruthy();
  expect(within(builder).getByRole("heading", { name: "source_event_id" })).toBeTruthy();
  expect((within(builder).getByRole("radio", { name: "Fixed value" }) as HTMLInputElement).checked).toBe(true);
  fireEvent.click(within(builder).getByRole("radio", { name: "From input" }));
  expect(within(builder).getByLabelText("Read from")).toBeTruthy();
  expect(within(builder).getByLabelText("Event type header")).toBeTruthy();
  expect(within(builder).getByLabelText("Prefix (optional)")).toBeTruthy();
  // Two decisions and a verdict: no envelope to read, no button to ask, no requirements to author.
  expect(within(builder).queryByRole("heading", { name: "Normalized Event" })).toBeNull();
  expect(within(builder).queryByRole("button", { name: /Preview/ })).toBeNull();
  expect(within(builder).queryByText(/Input requirements/)).toBeNull();
});

it("shows broker messages without HTTP request context", async () => {
  renderScreen(
    <EventBuilder
      contractKey="broker Source"
      sourceType="broker"
      draft={{ expression: "", identity: null }}
      onUse={() => undefined}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });
  expect(within(builder).getByRole("heading", { name: "Sample message" })).toBeTruthy();
  expect(within(builder).queryByText("Request headers")).toBeNull();
  expect(within(builder).getByLabelText("Message body (JSON)")).toBeTruthy();
  fireEvent.click(within(builder).getByRole("radio", { name: "From input" }));
  expect(within(builder).queryByLabelText("Read from")).toBeNull();
  expect(within(builder).getByLabelText("Event type field")).toBeTruthy();
});

it("returns the ephemeral Builder draft to its owning Source form", async () => {
  const source = await openSource();
  fireEvent.click(within(source).getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });
  const useConfiguration = within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement;
  expect(useConfiguration.disabled).toBe(true);
  fireEvent.change(within(builder).getByLabelText("Event type"), { target: { value: "github.push" } });
  expect(useConfiguration.disabled).toBe(false);
  fireEvent.click(useConfiguration);
  await waitFor(() => expect(screen.queryByRole("dialog", { name: "Integrios Event Builder" })).toBeNull());
  expect(within(source).queryByText("Raw event contract")).toBeNull();
  fireEvent.click(within(source).getByRole("button", { name: "Open Integrios Event Builder" }));
  const reopened = await screen.findByRole("dialog", { name: "Integrios Event Builder" });
  fireEvent.click(within(reopened).getByRole("button", { name: "Advanced JSONata" }));
  expect((within(reopened).getByLabelText("Source mapping expression") as HTMLTextAreaElement).value).toContain(
    '"github.push"',
  );
});

/// A Source the guided form authored has to reopen in the form that authored it. Without the
/// inverse the Builder lands in the advanced editor and offers to reset an expression it wrote
/// itself, so the guided rule is unreachable the moment the Operator leaves the page.
it("reopens a stored guided mapping in the form that wrote it", async () => {
  const expression = guidedExpression({ source: "body", path: "event.type", prefix: "acme" });
  renderScreen(
    <EventBuilder
      contractKey="broker Source"
      sourceType="broker"
      draft={{ expression, identity: null }}
      onUse={() => undefined}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });

  expect((within(builder).getByRole("radio", { name: "From input" }) as HTMLInputElement).checked).toBe(true);
  expect((within(builder).getByLabelText("Event type field") as HTMLSelectElement).value).toBe("event.type");
  expect((within(builder).getByLabelText("Prefix (optional)") as HTMLInputElement).value).toBe("acme");
  expect(within(builder).queryByRole("button", { name: "Reset to guided" })).toBeNull();
});

it("keeps an expression it did not write in the advanced editor", async () => {
  renderScreen(
    <EventBuilder
      contractKey="broker Source"
      sourceType="broker"
      draft={{ expression: '{ "event_type": kind, "payload": body.inner }', identity: null }}
      onUse={() => undefined}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });

  expect((within(builder).getByLabelText("Source mapping expression") as HTMLTextAreaElement).value).toBe(
    '{ "event_type": kind, "payload": body.inner }',
  );
  // Returning would replace it, so the way back is the confirming control, not the plain one.
  expect(within(builder).getByRole("button", { name: "Reset to guided" })).toBeTruthy();
  expect(within(builder).queryByRole("button", { name: "Guided mode" })).toBeNull();
});

/// The identity rule runs before the mapping and is part of the Event that would be accepted, so the
/// preview resolves it through the same extractor Ingestion uses rather than the browser guessing at
/// a JSON Pointer of its own.
/// The Operator's question is whether the sample would be accepted, and as what. The answer comes
/// from the Admin API, which resolves the identity with the extractor ingestion uses, and it arrives
/// without being asked for once the configuration is whole.
it("says, unasked, whether the sample would be accepted and as what", async () => {
  const calls = stubHttp(({ url }) =>
    url.pathname.endsWith("/source-contracts/preview")
      ? { status: 200, body: { output: { event_type: "order.placed", payload: {} }, source_event_id: "d-7" } }
      : { status: 200, body: page([]) },
  );
  renderScreen(
    <EventBuilder
      contractKey="broker Source"
      sourceType="broker"
      draft={{ expression: "", identity: null }}
      onUse={() => undefined}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });

  fireEvent.change(within(builder).getByLabelText("Message body (JSON)"), {
    target: { value: '{"delivery":{"id":"d-7"}}' },
  });
  fireEvent.change(within(builder).getByLabelText("Event type"), { target: { value: "order.placed" } });
  fireEvent.change(within(builder).getByLabelText("Event identity"), { target: { value: "json_path" } });
  // The sample is analysed after the Operator stops typing, so the field it discovers is offered
  // only once that has run.
  await waitFor(() =>
    expect(within(builder).getByLabelText("Identity field").querySelector('option[value="/delivery/id"]')).toBeTruthy(),
  );
  fireEvent.change(within(builder).getByLabelText("Identity field"), { target: { value: "/delivery/id" } });

  const verdict = within(builder).getByRole("status");
  await waitFor(() => expect(verdict.textContent).toContain("Accepted as order.placed"), {
    timeout: 3000,
  });
  expect(verdict.textContent).toContain("Identity d-7.");
  const preview = calls.filter((call) => call.url.pathname.endsWith("/source-contracts/preview")).at(-1)!;
  expect((preview.body as Record<string, unknown>).event_identity_rule).toEqual({
    kind: "json_path",
    value: "/delivery/id",
    allow_missing: false,
  });
});

it("names the API's reason when the sample would be rejected, and where an input requirement lives", async () => {
  stubHttp(({ url }) =>
    url.pathname.endsWith("/source-contracts/preview")
      ? {
          status: 400,
          body: {
            title: "One or more validation errors occurred.",
            status: 400,
            errors: { schema: ["sample_input field 'result' is required."] },
          },
        }
      : { status: 200, body: page([]) },
  );
  renderScreen(
    <EventBuilder
      contractKey="webhook Source"
      sourceType="webhook"
      draft={{
        expression: guidedExpression({ source: "fixed", value: "storefront.webhook.received" }),
        schema: { type: "object", required: ["result"], properties: { result: { type: "string" } } },
        identity: null,
      }}
      onUse={() => undefined}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });

  fireEvent.change(within(builder).getByLabelText("Request body (JSON)"), {
    target: { value: '{"delivery_id":"d-1"}' },
  });
  const verdict = within(builder).getByRole("status");
  await waitFor(() => expect(verdict.textContent).toContain("Rejected. sample_input field 'result' is required."), {
    timeout: 3000,
  });
  expect(verdict.textContent).not.toContain("One or more validation errors occurred.");
  expect(verdict.textContent).toContain("input requirements, edited under Raw event contract");
});

/// The Builder authors no input requirements, so a stored document it cannot author must leave
/// exactly as it arrived - including the additionalProperties the old requirement rows rewrote.
it("hands a stored input-requirements document back untouched", async () => {
  stubHttp(() => ({ status: 200, body: page([]) }));
  const schema = {
    type: "object",
    required: ["delivery_id", "result"],
    properties: { result: { type: "string" }, delivery_id: { type: "string" } },
    additionalProperties: false,
  };
  const onUse = vi.fn();
  renderScreen(
    <EventBuilder
      contractKey="webhook Source"
      sourceType="webhook"
      draft={{ expression: "", schema, identity: null }}
      onUse={onUse}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });
  fireEvent.change(within(builder).getByLabelText("Event type"), { target: { value: "storefront.webhook.received" } });
  fireEvent.click(within(builder).getByRole("button", { name: "Use configuration" }));

  expect(onUse).toHaveBeenCalledOnce();
  expect(onUse.mock.calls[0][0].schema).toStrictEqual({
    type: "object",
    required: ["delivery_id", "result"],
    properties: { result: { type: "string" }, delivery_id: { type: "string" } },
    additionalProperties: false,
  });
});

/// A guided rule the Operator has left behind is not a reason to hold back an expression they wrote
/// by hand: the advanced editor reads whatever it likes, and the stale rule addresses nothing in it.
it("does not hold back an advanced expression for a guided rule it no longer represents", async () => {
  renderScreen(
    <EventBuilder
      contractKey="webhook Source"
      sourceType="webhook"
      draft={{ expression: "", identity: null }}
      onUse={() => undefined}
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });

  fireEvent.change(within(builder).getByLabelText("Header 1 name"), { target: { value: "x-kind" } });
  fireEvent.change(within(builder).getByLabelText("Header 1 sample value"), { target: { value: "created" } });
  fireEvent.click(within(builder).getByRole("radio", { name: "From input" }));
  await waitFor(() =>
    expect(within(builder).getByLabelText("Event type header").querySelector('option[value="x-kind"]')).toBeTruthy(),
  );
  fireEvent.change(within(builder).getByLabelText("Event type header"), { target: { value: "x-kind" } });

  fireEvent.click(within(builder).getByRole("button", { name: "Advanced JSONata" }));
  fireEvent.change(within(builder).getByLabelText("Source mapping expression"), {
    target: { value: '{ "event_type": "fixed.thing", "payload": $ }' },
  });
  // The header the abandoned rule read is gone from the sample.
  fireEvent.change(within(builder).getByLabelText("Header 1 name"), { target: { value: "" } });
  fireEvent.change(within(builder).getByLabelText("Header 1 sample value"), { target: { value: "" } });

  await waitFor(() =>
    expect((within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement).disabled).toBe(
      false,
    ),
  );
});

async function openBuilder(
  sourceType: "webhook" | "broker",
  draft: SourceContractDraft = { expression: "", identity: null },
) {
  renderScreen(
    <EventBuilder contractKey={`${sourceType} Source`} sourceType={sourceType} draft={draft} onUse={() => undefined} />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open Integrios Event Builder" }));
  return screen.findByRole("dialog", { name: "Integrios Event Builder" });
}

const acceptedAs = (eventType: string) => ({
  status: 200,
  body: { output: { event_type: eventType, payload: {} }, source_event_id: null },
});

/// The answer does not blink out while the next one is worked out: the last verdict stays, marked
/// busy so a screen reader hears only the settled one, and is replaced when the new answer lands.
it("keeps the last verdict on screen, busy, while a change is checked", async () => {
  const pending: Array<() => void> = [];
  stubHttp(({ url, body }) => {
    if (!url.pathname.endsWith("/source-contracts/preview")) return { status: 200, body: page([]) };
    const expression = ((body as { mapping: { expression: string } }).mapping.expression ?? "") as string;
    const answer = acceptedAs(expression.includes("second") ? "second" : "first");
    if (!expression.includes("second")) return answer;
    return new Promise((resolve) => pending.push(() => resolve(answer)));
  });
  const builder = await openBuilder("broker");
  const verdict = within(builder).getByRole("status");

  fireEvent.change(within(builder).getByLabelText("Event type"), { target: { value: "first" } });
  fireEvent.change(within(builder).getByLabelText("Message body (JSON)"), { target: { value: '{"id":1}' } });

  await waitFor(() => expect(verdict.textContent).toContain("Accepted as first"), { timeout: 3000 });
  expect(verdict.getAttribute("aria-busy")).toBe("false");

  fireEvent.change(within(builder).getByLabelText("Event type"), { target: { value: "second" } });
  // Straight after the edit, and while the request is out, the first answer is still what reads.
  expect(verdict.textContent).toContain("Accepted as first");
  expect(verdict.getAttribute("aria-busy")).toBe("true");
  await waitFor(() => expect(pending.length).toBe(1), { timeout: 3000 });
  expect(verdict.textContent).toContain("Accepted as first");
  expect(verdict.textContent).not.toContain("Checking");

  pending[0]();
  await waitFor(() => expect(verdict.textContent).toContain("Accepted as second"));
  expect(verdict.getAttribute("aria-busy")).toBe("false");
});

/// A sample is one example. A rule reading a header this sample lacks can be right for the requests
/// that will arrive, so the verdict says what happens to this sample and the configuration can still
/// be used.
it("reports a sample that lacks the chosen header without holding the configuration back", async () => {
  stubHttp(({ url }) =>
    url.pathname.endsWith("/source-contracts/preview")
      ? {
          status: 400,
          body: { status: 400, errors: { mapping: ["This request does not contain a valid Event type."] } },
        }
      : { status: 200, body: page([]) },
  );
  const builder = await openBuilder("webhook", {
    expression: guidedExpression({ source: "header", header: "x-github-event", prefix: "github" }),
    identity: null,
  });
  fireEvent.change(within(builder).getByLabelText("Request body (JSON)"), {
    target: { value: '{"ref":"refs/heads/main"}' },
  });

  const verdict = within(builder).getByRole("status");
  await waitFor(
    () => expect(verdict.textContent).toContain("Rejected. This request does not contain a valid Event type."),
    {
      timeout: 3000,
    },
  );
  expect(within(builder).getByText(/This sample does not carry x-github-event/)).toBeTruthy();
  expect((within(builder).getByRole("button", { name: "Use configuration" }) as HTMLButtonElement).disabled).toBe(
    false,
  );
});

/// With only an identity chosen the Source has no mapping, and ingestion takes the input itself as
/// the Event. That is checked too, because a provider's own JSON never is one.
it("checks a Source with no Event type rule as taking its input as the Event", async () => {
  const calls = stubHttp(({ url }) =>
    url.pathname.endsWith("/source-contracts/preview")
      ? {
          status: 400,
          body: { status: 400, errors: { sample_input: ["Source mapping output contains unsupported field 'ref'."] } },
        }
      : { status: 200, body: page([]) },
  );
  const builder = await openBuilder("broker");
  fireEvent.change(within(builder).getByLabelText("Event identity"), { target: { value: "message_id" } });
  fireEvent.change(within(builder).getByLabelText("Message body (JSON)"), {
    target: { value: '{"ref":"refs/heads/main"}' },
  });

  const verdict = within(builder).getByRole("status");
  await waitFor(() => expect(verdict.textContent).toContain("Rejected."), { timeout: 3000 });
  expect(verdict.textContent).toContain("With no Event type rule, each message must already be an Integrios Event.");
  const preview = calls.filter((call) => call.url.pathname.endsWith("/source-contracts/preview")).at(-1)!;
  expect((preview.body as Record<string, unknown>).mapping).toBeNull();
});

/// The identity rule runs before the input is looked at as an Event, so a refusal from it says nothing
/// about the missing Event type rule, and the dialog must not suggest that it does.
it("does not blame a missing Event type rule for an identity refusal", async () => {
  stubHttp(({ url }) =>
    url.pathname.endsWith("/source-contracts/preview")
      ? {
          status: 400,
          body: {
            status: 400,
            errors: {
              event_identity_rule: [
                "The request has no 'x-github-delivery' header to read its Source Event identity from.",
              ],
            },
          },
        }
      : { status: 200, body: page([]) },
  );
  const builder = await openBuilder("webhook", {
    expression: "",
    identity: { kind: "header", value: "x-github-delivery", allowMissing: false },
  });
  fireEvent.change(within(builder).getByLabelText("Request body (JSON)"), {
    target: { value: '{"ref":"refs/heads/main"}' },
  });

  const verdict = within(builder).getByRole("status");
  await waitFor(() => expect(verdict.textContent).toContain("no 'x-github-delivery' header"), { timeout: 3000 });
  expect(verdict.textContent).not.toContain("With no Event type rule");
  expect(verdict.textContent).not.toContain("input requirements");
});

/// Opening a saved Source shows its rule beside an empty sample. That is nothing to judge, so the
/// dialog asks for a sample instead of rejecting a request nobody sent, and asks the API nothing.
it("asks for a sample before checking anything", async () => {
  const calls = stubHttp(() => ({ status: 200, body: page([]) }));
  const builder = await openBuilder("webhook", {
    expression: guidedExpression({ source: "header", header: "x-github-event", prefix: "github" }),
    identity: { kind: "header", value: "x-github-delivery", allowMissing: false },
  });
  const verdict = within(builder).getByRole("status");
  expect((within(builder).getByLabelText("Identity header") as HTMLSelectElement).value).toBe("x-github-delivery");

  await new Promise((resolve) => setTimeout(resolve, 900));
  expect(verdict.textContent).toContain("Add a sample request to check whether Integrios would accept it.");
  expect(verdict.textContent).not.toContain("Rejected");
  expect(calls.some((call) => call.url.pathname.endsWith("/source-contracts/preview"))).toBe(false);

  fireEvent.change(within(builder).getByLabelText("Header 1 name"), { target: { value: "x-github-event" } });
  await waitFor(() => expect(verdict.textContent).not.toContain("Add a sample"));
});

/// Clearing a saved choice must not strand it. The sample need not carry what the Source reads, so
/// the saved header stays offered after the Operator picks "Choose a header…", and can be picked back.
it("keeps a saved header choosable after it is cleared", async () => {
  stubHttp(() => ({ status: 200, body: page([]) }));
  const builder = await openBuilder("webhook", {
    expression: guidedExpression({ source: "header", header: "x-github-event", prefix: "github" }),
    identity: { kind: "header", value: "x-github-delivery", allowMissing: false },
  });
  const optionValues = (label: string) =>
    [...(within(builder).getByLabelText(label) as HTMLSelectElement).options].map((option) => option.value);

  for (const [label, saved] of [
    ["Identity header", "x-github-delivery"],
    ["Event type header", "x-github-event"],
  ] as const) {
    const picker = within(builder).getByLabelText(label) as HTMLSelectElement;
    fireEvent.change(picker, { target: { value: "" } });
    expect(picker.value).toBe("");
    expect(optionValues(label)).toContain(saved);
    fireEvent.change(picker, { target: { value: saved } });
    expect((within(builder).getByLabelText(label) as HTMLSelectElement).value).toBe(saved);
  }
});

const capturedCurl = String.raw`curl -X 'POST' 'https://webhook.site/5f0c6a3e' \
  -H 'X-GitHub-Event: issues' \
  -H 'x-github-delivery: 72d3162e-cc78-11e3-81ab-4c9367dc0958' \
  -H 'authorization: Bearer  secret' \
  -d $'{\n  "action": "opened",\n  "issue": { "title": "It\'s broken" }\n}'`;

it("fills the sample request from a pasted curl command", async () => {
  stubHttp(() => ({ status: 200, body: page([]) }));
  const builder = await openBuilder("webhook");
  fireEvent.click(within(builder).getByRole("button", { name: "Import cURL" }));
  fireEvent.change(within(builder).getByLabelText("curl command"), { target: { value: capturedCurl } });
  fireEvent.click(within(builder).getByRole("button", { name: "Import" }));

  const value = (label: string) => (within(builder).getByLabelText(label) as HTMLInputElement).value;
  expect([1, 2, 3].map((row) => [value(`Header ${row} name`), value(`Header ${row} sample value`)])).toEqual([
    ["X-GitHub-Event", "issues"],
    ["x-github-delivery", "72d3162e-cc78-11e3-81ab-4c9367dc0958"],
    ["authorization", "Bearer  secret"],
  ]);
  expect(within(builder).queryByLabelText("Header 4 name")).toBeNull();
  expect(value("Request body (JSON)")).toBe('{\n  "action": "opened",\n  "issue": { "title": "It\'s broken" }\n}');
  expect(within(builder).queryByLabelText("curl command")).toBeNull();

  const options = (label: string) =>
    [...(within(builder).getByLabelText(label) as HTMLSelectElement).options].map((option) => option.textContent);
  fireEvent.click(within(builder).getByRole("radio", { name: "From input" }));
  expect(options("Event type header")).toContain("x-github-event");
  fireEvent.change(within(builder).getByLabelText("Read from"), { target: { value: "body" } });
  await waitFor(() => expect(options("Event type field")).toContain("issue.title"));
  fireEvent.change(within(builder).getByLabelText("Event identity"), { target: { value: "header" } });
  expect(options("Identity header")).toContain("x-github-delivery");
  fireEvent.change(within(builder).getByLabelText("Event identity"), { target: { value: "json_path" } });
  expect(options("Identity field")).toContain("action");
});

it("says so, and changes nothing, when a paste carries no headers or body", async () => {
  stubHttp(() => ({ status: 200, body: page([]) }));
  const builder = await openBuilder("webhook");
  fireEvent.change(within(builder).getByLabelText("Header 1 name"), { target: { value: "x-kept" } });
  fireEvent.click(within(builder).getByRole("button", { name: "Import cURL" }));
  fireEvent.change(within(builder).getByLabelText("curl command"), {
    target: { value: "curl -X POST https://webhook.site/5f0c6a3e" },
  });
  fireEvent.click(within(builder).getByRole("button", { name: "Import" }));

  expect(within(builder).getByRole("alert").textContent).toContain("No headers or body found");
  expect(within(builder).getByLabelText("curl command")).toBeTruthy();

  fireEvent.click(within(builder).getByRole("button", { name: "Back to sample" }));
  expect((within(builder).getByLabelText("Header 1 name") as HTMLInputElement).value).toBe("x-kept");
  expect((within(builder).getByLabelText("Request body (JSON)") as HTMLTextAreaElement).value).toBe("{}");
});

it("offers no curl import for a broker message", async () => {
  const builder = await openBuilder("broker");
  expect(within(builder).queryByRole("button", { name: "Import cURL" })).toBeNull();
});
