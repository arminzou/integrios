import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { type Call, page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { TenantScreen, TenantsScreen } from "./Tenants";

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
  it("places the name search before the compact filters", async () => {
    stubHttp(() => ({ status: 200, body: page([]) }));

    renderScreen(<TenantsScreen />, "/tenants");

    const filters = await screen.findByRole("region", { name: "Filters" });
    const name = within(filters).getByRole("searchbox", { name: "Name or slug" });
    expect(within(filters).getByRole("searchbox", { name: "Environment" })).toBeTruthy();
    // The name search leads the row; how it is drawn is measured in the browser, where there is
    // layout and colour to measure, rather than pinned here as a list of class names.
    expect(filters.querySelector("input, button")).toBe(name);
    expect(name.getAttribute("placeholder")).toBe("Name or slug");
  });

  it.each(["Name or slug", "Environment"])(
    "applies %s and restarts paging, then restores the URL value",
    async (label) => {
      const parameter = label === "Environment" ? "environment" : "name";
      const calls = stubHttp(({ url }) => ({
        status: 200,
        body: page([tenant({ name: url.searchParams.has("after") ? "Second" : "Acme" })], "cursor-1"),
      }));
      const { router } = renderScreen(<TenantsScreen />, "/tenants");
      await screen.findByRole("link", { name: "Acme" });
      fireEvent.click(screen.getByRole("button", { name: "Load more" }));
      await screen.findByRole("link", { name: "Second" });
      fireEvent.change(screen.getByLabelText(label), { target: { value: "  production  " } });
      fireEvent.submit(screen.getByLabelText(label).closest("form")!);
      await waitFor(() => expect(listCalls(calls).at(-1)!.url.searchParams.get(parameter)).toBe("production"));
      expect(listCalls(calls).at(-1)!.url.searchParams.has("after")).toBe(false);
      expect(router.state.location.search).toContain(`${parameter}=production`);
      await act(() => router.navigate(`/tenants?${parameter}=restored`));
      await waitFor(() => expect((screen.getByLabelText(label) as HTMLInputElement).value).toBe("restored"));
    },
  );

  it("reports a request that could not reach Admin instead of loading forever", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("offline")));

    renderScreen(<TenantsScreen />);

    expect((await screen.findByRole("alert")).textContent).toContain("The Admin API could not be reached.");
    expect(screen.queryByText("Loading…")).toBeNull();
  });

  it("appends the next page only when Load more is used, and sends the cursor it was given", async () => {
    const calls = stubHttp(({ url }) =>
      url.searchParams.get("after") === "cursor-1"
        ? {
            status: 200,
            body: page([tenant({ id: `${"2".repeat(8)}-2222-2222-2222-222222222222`, name: "Beta", slug: "beta" })]),
          }
        : { status: 200, body: page([tenant()], "cursor-1") },
    );

    renderScreen(<TenantsScreen />);
    await screen.findByRole("link", { name: "Acme" });
    expect(listCalls(calls)[0].url.searchParams.has("after")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Load more" }));

    await screen.findByRole("link", { name: "Beta" });
    // The first page is still on screen: Load more appends, it does not replace.
    expect(screen.getByRole("link", { name: "Acme" })).toBeTruthy();
    expect(listCalls(calls)[1].url.searchParams.get("after")).toBe("cursor-1");
  });

  it("never lets a slow read under the previous filter land in the list it was replaced by", async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: Request | string) => {
        const url = new URL(typeof input === "string" ? input : input.url, "http://localhost");
        const filtered = url.searchParams.get("status") === "disabled";
        // The read the Operator has moved on from answers last, which is the ordering that used to
        // be able to overwrite the newer one.
        if (!filtered) await held;
        return new Response(
          JSON.stringify(
            page([
              filtered
                ? tenant({ id: `${"2".repeat(8)}-2222-2222-2222-222222222222`, name: "Beta", slug: "beta" })
                : tenant(),
            ]),
          ),
          { status: 200, headers: { "content-type": "application/json" } },
        );
      }),
    );

    const { router } = renderScreen(<TenantsScreen />, "/tenants");
    await screen.findByLabelText("Status");
    await act(() => router.navigate("/tenants?status=disabled"));
    await screen.findByRole("link", { name: "Beta" });

    await act(async () => {
      release();
    });

    expect(screen.queryByRole("link", { name: "Acme" })).toBeNull();
    expect(screen.getByRole("link", { name: "Beta" })).toBeTruthy();
  });

  it("restarts from the first cursor when a filter changes instead of reusing the old one", async () => {
    const calls = stubHttp(({ url }) =>
      url.searchParams.get("status") === "disabled"
        ? { status: 200, body: page([]) }
        : { status: 200, body: page([tenant()], "cursor-1") },
    );

    const { router } = renderScreen(<TenantsScreen />, "/tenants");
    await screen.findByRole("link", { name: "Acme" });

    await act(() => router.navigate("/tenants?status=disabled"));

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

/// The overview screen reads two endpoints, and every test here needs a shape from both. The
/// activity summary is deliberately an empty window: what the attention banner reports is the
/// outstanding count, and it must not depend on anything the rolling hour happened to catch.
const stubOverview = (overview: Record<string, number>) =>
  stubHttp(({ url }) => {
    if (url.pathname.endsWith("/overview"))
      return {
        status: 200,
        body: {
          topics: 1,
          destinations: 1,
          sources: 1,
          subscriptions: 1,
          live_api_keys: 1,
          dead_lettered_deliveries: 0,
          ingestion_endpoint: "http://localhost:5231/",
          ...overview,
        },
      };
    if (url.pathname.endsWith("/activity-summary"))
      return {
        status: 200,
        body: {
          events_accepted: 0,
          awaiting_routing: 0,
          unrouted: 0,
          dead_lettered_deliveries: 0,
          window_start: "2026-09-06T16:00:00Z",
          window_end: "2026-09-06T17:00:00Z",
        },
      };
    return { status: 200, body: tenant() };
  });

describe("The Tenant overview's attention banner", () => {
  it("counts Deliveries that are still dead-lettered, not only ones that failed in the last hour", async () => {
    // The banner and the rail badge exist to take an Operator to work nobody has attended to. A
    // dead-lettered Delivery stays dead-lettered until it is replayed, so counting it inside the
    // activity summary's rolling hour hid every failure older than that — and reported zero, which
    // is the one answer a control for unattended work must never give while work is outstanding.
    stubOverview({ dead_lettered_deliveries: 9 });

    renderScreen(<TenantScreen tenantId={tenantId} />, `/tenants/${tenantId}`);

    expect(await screen.findByText("9 dead-lettered Deliveries")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Open Events" })).toBeTruthy();
  });
});

describe("Tenant Overview navigation", () => {
  it("keeps Tenant actions in the header and links each configuration summary", async () => {
    stubOverview({ destinations: 2, sources: 3, subscriptions: 4, live_api_keys: 5 });

    renderScreen(<TenantScreen tenantId={tenantId} />, `/tenants/${tenantId}`);

    const header = (await screen.findByRole("heading", { name: "Overview" })).closest("header")!;
    expect(within(header).getByRole("button", { name: "Edit" })).toBeTruthy();
    expect(within(header).getByRole("button", { name: "Deactivate" })).toBeTruthy();

    const summary = screen.getByRole("region", { name: "Configured in this Tenant" });
    const destinations = [
      ["Topics", `/tenants/${tenantId}/topics`],
      ["Destinations", `/tenants/${tenantId}/destinations`],
      ["Sources", `/tenants/${tenantId}/sources`],
      ["Subscriptions", `/tenants/${tenantId}/subscriptions`],
      ["Live API keys", `/tenants/${tenantId}/tenant-api-keys`],
    ] as const;
    for (const [name, href] of destinations)
      expect(
        within(summary)
          .getByRole("link", { name: new RegExp(name) })
          .getAttribute("href"),
      ).toBe(href);
  });
});

describe("Tenant authoring", () => {
  it("shows a rejected field's own message beside it and keeps what the Operator typed", async () => {
    stubHttp(({ method }) =>
      method === "POST"
        ? {
            status: 422,
            body: {
              title: "One or more validation errors occurred.",
              errors: { slug: ["A Tenant already uses this slug."] },
            },
          }
        : { status: 200, body: page([]) },
    );

    renderScreen(<TenantsScreen />);
    fireEvent.click(screen.getByText("New Tenant"));
    const slug = await screen.findByLabelText("Slug");
    fireEvent.change(slug, { target: { value: "acme" } });
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Acme" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Tenant" }));

    const message = await screen.findByText("A Tenant already uses this slug.");
    expect(slug.getAttribute("aria-invalid")).toBe("true");
    // What the control is described as, read the way a screen reader reads it: the message the
    // server sent, wherever the form primitive chose to put it.
    const described = (slug.getAttribute("aria-describedby") ?? "")
      .split(" ")
      .map((id) => document.getElementById(id)?.textContent ?? "")
      .join(" ");
    expect(described).toContain(message.textContent);
    // A rejected create must not look like a success by clearing the form.
    expect((slug as HTMLInputElement).value).toBe("acme");
  });

  it("names the Tenant before deactivating it and calls nothing until it is confirmed", async () => {
    const calls = stubHttp(({ method }) => (method === "POST" ? { status: 200 } : { status: 200, body: tenant() }));

    renderScreen(<TenantScreen tenantId={tenantId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Deactivate" }));

    expect(screen.getByText(/Deactivate the Tenant "Acme" \(acme\)\?/)).toBeTruthy();
    expect(calls.some((call) => call.method === "POST")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Deactivate Acme" }));

    await waitFor(() => expect(calls.some((call) => call.method === "POST")).toBe(true));
    expect(calls.find((call) => call.method === "POST")!.url.pathname).toBe(`/admin/tenants/${tenantId}/deactivate`);
  });
});

describe("Deactivating a Tenant", () => {
  it("keeps the confirmation after the control that offered it has gone", async () => {
    // Deactivation removes the very control that performed it, and changes `updated_at`, which
    // remounts the edit panel and discards its mutation state. A notice living on either would be
    // gone at the moment it finally had something to report.
    let status = "active";
    stubHttp(({ url, method }) => {
      if (method === "POST" && url.pathname.endsWith("/deactivate")) {
        status = "disabled";
        return { status: 202 };
      }
      return { status: 200, body: tenant({ status, updated_at: `2026-09-01T00:00:0${status.length}Z` }) };
    });

    renderScreen(<TenantScreen tenantId={tenantId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Deactivate" }));
    fireEvent.click(screen.getByRole("button", { name: "Deactivate Acme" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "Deactivate" })).toBeNull());
    expect(screen.getByText("Tenant deactivated.")).toBeTruthy();
  });
});

describe("Filtering the Tenants list", () => {
  it("reads its filter from the URL and offers a way out of an empty filtered list", async () => {
    const calls = stubHttp(() => ({ status: 200, body: { items: [], next_cursor: null } }));

    const { router } = renderScreen(<TenantsScreen />, "/tenants?status=disabled");

    await waitFor(() => expect(calls.some((call) => call.url.searchParams.get("status") === "disabled")).toBe(true));
    expect((await screen.findByLabelText("Status")).textContent).toContain("Disabled");

    // An empty list that is empty because of the filter says how to stop filtering.
    fireEvent.click(await screen.findByRole("link", { name: "Clear filters" }));
    await waitFor(() => expect(router.state.location.search).toBe(""));
  });
});
