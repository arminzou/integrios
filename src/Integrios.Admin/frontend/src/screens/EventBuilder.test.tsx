import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { SourcesScreen } from "./Sources";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const connectorId = "33333333-3333-3333-3333-333333333333";

function stubOptions() {
  stubHttp(({ url }) => {
    if (url.pathname.endsWith("/connectors"))
      return { status: 200, body: page([{ id: connectorId, name: "GitHub", status: "active" }]) };
    if (url.pathname.endsWith("/topics"))
      return { status: 200, body: page([{ id: topicId, name: "orders", status: "active" }]) };
    return { status: 200, body: page([]) };
  });
}

async function openSource(type: "event_api" | "webhook" | "queue" = "webhook") {
  stubOptions();
  renderScreen(<SourcesScreen tenantId={tenantId} />);
  fireEvent.click(screen.getByRole("button", { name: "New Source" }));
  const dialog = await screen.findByRole("dialog", { name: "New Source" });
  fireEvent.change(within(dialog).getByLabelText("Connector"), { target: { value: connectorId } });
  fireEvent.change(within(dialog).getByLabelText("Topic"), { target: { value: topicId } });
  fireEvent.change(within(dialog).getByLabelText("Type"), { target: { value: type } });
  return dialog;
}

it("hosts the Event Builder in a webhook Source draft", async () => {
  const webhook = await openSource();
  expect(within(webhook).getByRole("button", { name: "Open Integrios Event Builder" })).toBeTruthy();
});

it("returns the ephemeral Builder draft to its owning Source form", async () => {
  const source = await openSource();
  fireEvent.click(within(source).getByRole("button", { name: "Open Integrios Event Builder" }));
  const builder = await screen.findByRole("dialog", { name: "Integrios Event Builder" });
  fireEvent.change(within(builder).getByLabelText("Text prefix"), { target: { value: "github" } });
  fireEvent.click(within(builder).getByRole("button", { name: "Use configuration" }));
  await waitFor(() => expect(screen.queryByRole("dialog", { name: "Integrios Event Builder" })).toBeNull());
  expect((within(source).getByLabelText("Event mapping (JSONata, optional)") as HTMLTextAreaElement).value).toContain(
    '"github"',
  );
});
