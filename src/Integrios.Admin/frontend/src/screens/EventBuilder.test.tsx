import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { EventBuilder } from "./EventBuilder";
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
  const normalized = within(builder).getByRole("heading", { name: "Normalized Event" }).closest("section")!;
  expect(within(normalized).getByText(/supplied by Event identity when configured/)).toBeTruthy();
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
