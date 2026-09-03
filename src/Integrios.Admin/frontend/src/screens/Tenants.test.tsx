import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { TenantScreen, TenantsScreen } from "./Tenants";
import { stubHttp, page, type Call } from "../test/http";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";

function tenant(overrides: Record<string, unknown> = {}) {
  return {
    id: tenantId,
    slug: "acme",
    name: "Acme",
    status: "active",
    environment: "production",
    description: null,
    created_at: "2026-09-01T00:00:00Z",
    updated_at: "2026-09-01T00:00:00Z",
    ...overrides,
  };
}

const listCalls = (calls: Call[]) => calls.filter((call) => call.method === "GET");

describe("Tenants list", () => {
  it("reports a request that could not reach Admin instead of loading forever", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("offline")));

    render(<TenantsScreen />);

    expect((await screen.findByRole("alert")).textContent).toContain("The Admin API could not be reached.");
    expect(screen.queryByText("Loading…")).toBeNull();
  });

  it("appends the next page only when Load more is used, and sends the cursor it was given", async () => {
    const calls = stubHttp(({ url }) =>
      url.searchParams.get("after") === "cursor-1"
        ? { status: 200, body: page([tenant({ id: "2".repeat(8) + "-2222-2222-2222-222222222222", name: "Beta", slug: "beta" })]) }
        : { status: 200, body: page([tenant()], "cursor-1") },
    );

    render(<TenantsScreen />);
    await screen.findByRole("link", { name: "Acme" });
    expect(listCalls(calls)[0].url.searchParams.has("after")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Load more" }));

    await screen.findByRole("link", { name: "Beta" });
    // The first page is still on screen: Load more appends, it does not replace.
    expect(screen.getByRole("link", { name: "Acme" })).toBeTruthy();
    expect(listCalls(calls)[1].url.searchParams.get("after")).toBe("cursor-1");
  });

  it("restarts from the first cursor when a filter changes instead of reusing the old one", async () => {
    const calls = stubHttp(({ url }) =>
      url.searchParams.get("status") === "disabled"
        ? { status: 200, body: page([]) }
        : { status: 200, body: page([tenant()], "cursor-1") },
    );

    render(<TenantsScreen />);
    await screen.findByRole("link", { name: "Acme" });

    fireEvent.change(screen.getByLabelText("Status"), { target: { value: "disabled" } });

    // The rows read under the previous filter are discarded immediately, not left on screen while
    // the new first page is still in flight.
    expect(screen.queryByRole("link", { name: "Acme" })).toBeNull();
    expect(screen.getByText("Loading…")).toBeTruthy();

    await screen.findByText("No Tenants match this filter.");
    const refetch = listCalls(calls).at(-1)!;
    expect(refetch.url.searchParams.get("status")).toBe("disabled");
    expect(refetch.url.searchParams.has("after")).toBe(false);
    // The rows read under the previous filter are discarded rather than left mixed in.
    expect(screen.queryByRole("link", { name: "Acme" })).toBeNull();
  });
});

describe("Tenant authoring", () => {
  it("shows a rejected field's own message beside it and keeps what the Operator typed", async () => {
    stubHttp(({ method }) =>
      method === "POST"
        ? {
            status: 422,
            body: { title: "One or more validation errors occurred.", errors: { slug: ["A Tenant already uses this slug."] } },
          }
        : { status: 200, body: page([]) },
    );

    render(<TenantsScreen />);
    const slug = await screen.findByLabelText("Slug");
    fireEvent.change(slug, { target: { value: "acme" } });
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Acme" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Tenant" }));

    const message = await screen.findByText("A Tenant already uses this slug.");
    expect(slug.getAttribute("aria-invalid")).toBe("true");
    expect(slug.getAttribute("aria-describedby")).toBe(message.id);
    // A rejected create must not look like a success by clearing the form.
    expect((slug as HTMLInputElement).value).toBe("acme");
  });

  it("names the Tenant before deactivating it and calls nothing until it is confirmed", async () => {
    const calls = stubHttp(({ method }) =>
      method === "POST" ? { status: 200 } : { status: 200, body: tenant() },
    );

    render(<TenantScreen tenantId={tenantId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Deactivate Tenant" }));

    expect(screen.getByText(/Deactivate the Tenant "Acme" \(acme\)\?/)).toBeTruthy();
    expect(calls.some((call) => call.method === "POST")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Deactivate Acme" }));

    await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
    expect(calls.find((call) => call.method === "POST")!.url.pathname).toBe(`/admin/tenants/${tenantId}/deactivate`);
  });
});
