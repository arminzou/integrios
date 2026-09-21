import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { TenantApiKeysScreen } from "./TenantApiKeys";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const keyId = "44444444-4444-4444-4444-444444444444";
const token = "itk_live_thisisthesecretvalue";

const listItem = {
  id: keyId,
  tenant_id: tenantId,
  name: "Ingest",
  key_prefix: "itk_live_ab",
  state: "active",
  description: null,
  created_at: "2026-09-01T00:00:00Z",
  last_used_at: null,
  revoked_at: null,
};

const revokedItem = { ...listItem, state: "revoked", revoked_at: "2026-09-10T00:00:00Z" };

describe("Tenant API keys", () => {
  it("shows a revoked key as revoked and offers deletion instead of revocation", async () => {
    stubHttp(({ url }) =>
      url.pathname.endsWith(`/tenant-api-keys/${keyId}`)
        ? { status: 200, body: { ...revokedItem, status: revokedItem.state } }
        : { status: 200, body: page([revokedItem]) },
    );

    renderScreen(<TenantApiKeysScreen tenantId={tenantId} selectedTenantApiKeyId={keyId} />);
    const table = await screen.findByRole("table");
    const inspector = await screen.findByRole("complementary", { name: "Tenant API key detail" });
    await within(inspector).findByText("Revoked", { selector: "dt" });

    expect(within(table).getByText("Revoked")).toBeTruthy();
    expect(within(inspector).queryByRole("button", { name: "Revoke" })).toBeNull();
    expect(within(inspector).getByRole("button", { name: "Delete" })).toBeTruthy();
  });

  it("creates a key from a name and description alone", async () => {
    const calls = stubHttp(({ method }) =>
      method === "POST"
        ? { status: 201, body: { tenant_api_key: { ...listItem, status: "active" }, token } }
        : { status: 200, body: page([listItem]) },
    );

    renderScreen(<TenantApiKeysScreen tenantId={tenantId} />);
    fireEvent.click(await screen.findByRole("button", { name: "New API key" }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "Ingest" } });
    fireEvent.click(screen.getByRole("button", { name: "Create API key" }));
    await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
    expect(calls.find((call) => call.method === "POST")!.body).toEqual({ name: "Ingest", description: null });
  });

  it("shows a new key once and stops showing it once it is dismissed", async () => {
    stubHttp(({ method }) =>
      method === "POST"
        ? { status: 201, body: { tenant_api_key: { ...listItem, status: "active" }, token } }
        : { status: 200, body: page([listItem]) },
    );

    renderScreen(<TenantApiKeysScreen tenantId={tenantId} />);
    fireEvent.click(await screen.findByRole("button", { name: "New API key" }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "Ingest" } });
    fireEvent.click(screen.getByRole("button", { name: "Create API key" }));

    expect(await screen.findByText(token)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    // The key exists in that one response and nowhere else: the reloaded list carries only a prefix.
    expect(screen.queryByText(token)).toBeNull();
    expect(await screen.findByText("itk_live_ab")).toBeTruthy();
  });

  it("names the key it is about to revoke and does not revoke until confirmed", async () => {
    // The panel reads the key by its own id, so the stub answers the detail path with a key rather
    // than with the list every other GET returns. Once the write has happened, both reads answer
    // with the key as revoked.
    let revoked = false;
    const calls = stubHttp(({ method, url }) => {
      if (method === "POST") {
        revoked = true;
        return { status: 200 };
      }
      const current = revoked ? revokedItem : listItem;
      if (url.pathname.endsWith(`/tenant-api-keys/${keyId}`))
        return { status: 200, body: { ...current, status: current.state } };
      return { status: 200, body: page([current]) };
    });

    // Revoke lives in the panel that names the key rather than on the row, so the key has to be
    // the selected one for the control to exist at all.
    const { router } = renderScreen(
      <TenantApiKeysScreen tenantId={tenantId} selectedTenantApiKeyId={keyId} />,
      `/tenants/${tenantId}/tenant-api-keys/${keyId}`,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Revoke" }));

    expect(screen.getByText(/Revoke the Tenant API key "Ingest" \(itk_live_ab\)\?/)).toBeTruthy();
    expect(calls.some((call) => call.method === "POST")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Revoke Ingest" }));

    await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
    expect(calls.find((call) => call.method === "POST")!.url.pathname).toBe(
      `/admin/tenants/${tenantId}/tenant-api-keys/${keyId}/revoke`,
    );

    // The detail stays open and re-reads the key as revoked, with nothing left to revoke.
    expect(await screen.findByText("Ingest revoked.")).toBeTruthy();
    await waitFor(() => expect(screen.queryByRole("button", { name: "Revoke" })).toBeNull());
    expect(router.state.location.pathname).toBe(`/tenants/${tenantId}/tenant-api-keys/${keyId}`);
  });

  it("deletes a revoked key only after confirmation and closes its detail", async () => {
    let deleted = false;
    const calls = stubHttp(({ method, url }) => {
      if (method === "DELETE") {
        deleted = true;
        return { status: 200 };
      }
      if (url.pathname.endsWith(`/tenant-api-keys/${keyId}`))
        return deleted ? { status: 404 } : { status: 200, body: revokedItem };
      return { status: 200, body: page(deleted ? [] : [revokedItem]) };
    });

    const { router } = renderScreen(
      <TenantApiKeysScreen tenantId={tenantId} selectedTenantApiKeyId={keyId} />,
      `/tenants/${tenantId}/tenant-api-keys/${keyId}`,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Delete" }));

    expect(screen.getByText(/Delete the revoked Tenant API key "Ingest" \(itk_live_ab\)\?/)).toBeTruthy();
    expect(calls.some((call) => call.method === "DELETE")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Delete Ingest" }));

    await waitFor(() => expect(calls.some((call) => call.method === "DELETE")).toBe(true));
    expect(calls.find((call) => call.method === "DELETE")!.url.pathname).toBe(
      `/admin/tenants/${tenantId}/tenant-api-keys/${keyId}`,
    );
    expect(await screen.findByText("Ingest deleted.")).toBeTruthy();
    await waitFor(() => expect(router.state.location.pathname).toBe(`/tenants/${tenantId}/tenant-api-keys`));
    await waitFor(() => expect(screen.queryByText("itk_live_ab")).toBeNull());
  });
});
