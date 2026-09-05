import { afterEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { TenantApiKeysScreen } from "./TenantApiKeys";
import { stubHttp, page } from "../test/http";

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
  expires_at: null,
  last_used_at: null,
};

describe("Tenant API keys", () => {
  it("shows a new key once and stops showing it once it is dismissed", async () => {
    stubHttp(({ method }) =>
      method === "POST"
        ? { status: 201, body: { tenant_api_key: { ...listItem, status: "active" }, token } }
        : { status: 200, body: page([listItem]) },
    );

    render(<TenantApiKeysScreen tenantId={tenantId} />);
    fireEvent.click(screen.getByText("New Tenant API key"));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "Ingest" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Tenant API key" }));

    expect(await screen.findByText(token)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "I have copied the key" }));

    // The key exists in that one response and nowhere else: the reloaded list carries only a prefix.
    expect(screen.queryByText(token)).toBeNull();
    expect(await screen.findByText("itk_live_ab")).toBeTruthy();
  });

  it("names the key it is about to revoke and does not revoke until confirmed", async () => {
    const calls = stubHttp(({ method }) =>
      method === "POST" ? { status: 200 } : { status: 200, body: page([listItem]) },
    );

    render(<TenantApiKeysScreen tenantId={tenantId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Revoke" }));

    expect(screen.getByText(/Revoke the Tenant API key "Ingest" \(itk_live_ab\)\?/)).toBeTruthy();
    expect(calls.some((call) => call.method === "POST")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Revoke Ingest" }));

    const revoke = await screen.findByRole("table");
    expect(revoke).toBeTruthy();
    expect(calls.find((call) => call.method === "POST")!.url.pathname).toBe(
      `/admin/tenants/${tenantId}/tenant-api-keys/${keyId}/revoke`,
    );
  });
});
