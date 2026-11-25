import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { page, quietBacklog, stubHttp } from "../test/http";
import { renderApp, renderScreen } from "../test/router";
import { SubscriberKeysScreen } from "./SubscriberKeys";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const destinationId = "22222222-2222-2222-2222-222222222222";
const keyId = "44444444-4444-4444-4444-444444444444";
const token = "intg_sub_thisisthesecretvalue";

const listItem = {
  id: keyId,
  tenant_id: tenantId,
  destination_id: destinationId,
  name: "Ingest",
  key_prefix: "intg_sub_ab12cd3",
  state: "active",
  description: null,
  created_at: "2026-09-01T00:00:00Z",
  last_used_at: null,
  revoked_at: null,
};

const revokedItem = { ...listItem, state: "revoked", revoked_at: "2026-09-10T00:00:00Z" };

describe("Subscriber keys", () => {
  it("shows a revoked key as revoked and offers deletion instead of revocation", async () => {
    stubHttp(({ url }) =>
      url.pathname.endsWith(`/subscriber-keys/${keyId}`)
        ? { status: 200, body: { ...revokedItem, status: revokedItem.state } }
        : { status: 200, body: page([revokedItem]) },
    );

    renderScreen(
      <SubscriberKeysScreen tenantId={tenantId} destinationId={destinationId} selectedSubscriberKeyId={keyId} />,
    );
    const table = await screen.findByRole("table");
    const inspector = await screen.findByRole("complementary", { name: "Subscriber key detail" });
    await within(inspector).findByText("Revoked", { selector: "dt" });

    expect(within(table).getByText("Revoked")).toBeTruthy();
    expect(within(inspector).queryByRole("button", { name: "Revoke" })).toBeNull();
    expect(within(inspector).getByRole("button", { name: "Delete" })).toBeTruthy();
  });

  it("creates a key from a name and description alone", async () => {
    const calls = stubHttp(({ method }) =>
      method === "POST"
        ? { status: 201, body: { subscriber_key: { ...listItem, status: "active" }, token } }
        : { status: 200, body: page([listItem]) },
    );

    renderScreen(<SubscriberKeysScreen tenantId={tenantId} destinationId={destinationId} />);
    fireEvent.click(await screen.findByRole("button", { name: "New Subscriber key" }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "Ingest" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Subscriber key" }));
    await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
    expect(calls.find((call) => call.method === "POST")!.body).toEqual({ name: "Ingest", description: null });
  });

  it("shows a new key once and stops showing it once it is dismissed", async () => {
    stubHttp(({ method }) =>
      method === "POST"
        ? { status: 201, body: { subscriber_key: { ...listItem, status: "active" }, token } }
        : { status: 200, body: page([listItem]) },
    );

    renderScreen(<SubscriberKeysScreen tenantId={tenantId} destinationId={destinationId} />);
    fireEvent.click(await screen.findByRole("button", { name: "New Subscriber key" }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "Ingest" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Subscriber key" }));

    expect(await screen.findByText(token)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    expect(screen.queryByText(token)).toBeNull();
    expect(await screen.findByText("intg_sub_ab12cd3")).toBeTruthy();
  });

  it("keeps a new key on screen when the session lapses right after it is created", async () => {
    let expired = false;
    stubHttp(({ method, url }) => {
      if (url.pathname === "/auth/session")
        return expired
          ? { status: 401 }
          : { status: 200, body: { antiforgery_token: "t", antiforgery_header_name: "X" } };
      if (expired) return { status: 401 };
      if (method === "POST") {
        expired = true;
        return { status: 201, body: { subscriber_key: { ...listItem, status: "active" }, token } };
      }
      if (url.pathname === `/admin/tenants/${tenantId}`) return { status: 200, body: { id: tenantId, name: "Acme" } };
      if (url.pathname.endsWith("/events/backlog")) return { status: 200, body: quietBacklog };
      return { status: 200, body: page([]) };
    });

    renderApp(`/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys`);
    fireEvent.click(await screen.findByRole("button", { name: "New Subscriber key" }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "Ingest" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Subscriber key" }));

    expect(await screen.findByText(token)).toBeTruthy();
    // An expired session moves the dashboard to sign-in on its next read, which would unmount the reveal.
    await expect(screen.findByRole("heading", { name: "Sign in to Integrios" }, { timeout: 300 })).rejects.toThrow();
    expect(screen.getByText(token)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(await screen.findByRole("heading", { name: "Sign in to Integrios" })).toBeTruthy();
  });

  it("names the key it is about to revoke and does not revoke until confirmed", async () => {
    let revoked = false;
    const calls = stubHttp(({ method, url }) => {
      if (method === "POST") {
        revoked = true;
        return { status: 200 };
      }
      const current = revoked ? revokedItem : listItem;
      if (url.pathname.endsWith(`/subscriber-keys/${keyId}`))
        return { status: 200, body: { ...current, status: current.state } };
      return { status: 200, body: page([current]) };
    });

    const { router } = renderScreen(
      <SubscriberKeysScreen tenantId={tenantId} destinationId={destinationId} selectedSubscriberKeyId={keyId} />,
      `/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys/${keyId}`,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Revoke" }));

    expect(screen.getByText(/Revoke the Subscriber key "Ingest" \(intg_sub_ab12cd3\)\?/)).toBeTruthy();
    expect(calls.some((call) => call.method === "POST")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Revoke Ingest" }));

    await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
    expect(calls.find((call) => call.method === "POST")!.url.pathname).toBe(
      `/admin/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys/${keyId}/revoke`,
    );

    expect(await screen.findByText("Ingest revoked.")).toBeTruthy();
    await waitFor(() => expect(screen.queryByRole("button", { name: "Revoke" })).toBeNull());
    expect(router.state.location.pathname).toBe(
      `/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys/${keyId}`,
    );
  });

  it("deletes a revoked key only after confirmation and closes its detail", async () => {
    let deleted = false;
    const calls = stubHttp(({ method, url }) => {
      if (method === "DELETE") {
        deleted = true;
        return { status: 200 };
      }
      if (url.pathname.endsWith(`/subscriber-keys/${keyId}`))
        return deleted ? { status: 404 } : { status: 200, body: revokedItem };
      return { status: 200, body: page(deleted ? [] : [revokedItem]) };
    });

    const { router } = renderScreen(
      <SubscriberKeysScreen tenantId={tenantId} destinationId={destinationId} selectedSubscriberKeyId={keyId} />,
      `/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys/${keyId}`,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Delete" }));

    expect(screen.getByText(/Delete the revoked Subscriber key "Ingest" \(intg_sub_ab12cd3\)\?/)).toBeTruthy();
    expect(calls.some((call) => call.method === "DELETE")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Delete Ingest" }));

    await waitFor(() => expect(calls.some((call) => call.method === "DELETE")).toBe(true));
    expect(calls.find((call) => call.method === "DELETE")!.url.pathname).toBe(
      `/admin/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys/${keyId}`,
    );
    expect(await screen.findByText("Ingest deleted.")).toBeTruthy();
    await waitFor(() =>
      expect(router.state.location.pathname).toBe(`/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys`),
    );
    await waitFor(() => expect(screen.queryByText("intg_sub_ab12cd3")).toBeNull());
  });
});
