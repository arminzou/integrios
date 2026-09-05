import { cleanup, screen, within } from "@testing-library/react";
import { matchRoutes } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { routeConfig } from "./routes";
import { page, stubHttp } from "./test/http";
import { renderApp } from "./test/router";

afterEach(cleanup);

const tenant = "11111111-1111-1111-1111-111111111111";
const topic = "22222222-2222-2222-2222-222222222222";
const subscription = "33333333-3333-3333-3333-333333333333";

/// The route the table resolves a URL to, named by its own path pattern. Asserting the pattern
/// rather than a rendered screen keeps these checks about routing alone.
function matched(pathname: string): string | undefined {
  return matchRoutes(routeConfig, pathname)?.at(-1)?.route.path;
}

describe("The route table", () => {
  it("keeps the Tenant from the path rather than inferring one", () => {
    expect(matched(`/tenants/${tenant}/sources`)).toBe("tenants/:tenantId/sources");
    expect(matched(`/tenants/${tenant}/connections/${topic}`)).toBe("tenants/:tenantId/connections/:connectionId");
  });

  it("carries every owning route value for a nested Subscription", () => {
    const match = matchRoutes(routeConfig, `/tenants/${tenant}/topics/${topic}/subscriptions/${subscription}`)?.at(-1);

    expect(match?.route.path).toBe("tenants/:tenantId/topics/:topicId/subscriptions/:subscriptionId");
    expect(match?.params).toEqual({ tenantId: tenant, topicId: topic, subscriptionId: subscription });
  });

  it("resolves the Event investigation routes under their Tenant", () => {
    expect(matched(`/tenants/${tenant}/events`)).toBe("tenants/:tenantId/events");
    expect(matched(`/tenants/${tenant}/events/${topic}`)).toBe("tenants/:tenantId/events/:eventId");
  });

  it("does not resolve a capability the dashboard does not own", () => {
    for (const path of [
      `/tenants/${tenant}/deliveries`,
      `/tenants/${tenant}/events/${topic}/attempts`,
      `/tenants/${tenant}/connections/${topic}/extra`,
      "/connectors/all/versions",
    ]) {
      expect(matched(path)).toBe("*");
    }
  });

  it("resolves the deployment root to the Tenants list", () => {
    expect(matched("/")).toBeUndefined(); // the index route carries no path of its own
    expect(matchRoutes(routeConfig, "/")?.at(-1)?.route.index).toBe(true);
    expect(matched("/tenants")).toBe("tenants");
  });
});

describe("Route values that are not identifiers", () => {
  const session = {
    user_id: tenant,
    display_name: "Operator",
    email: null,
    antiforgery_token: "t",
    antiforgery_header_name: "X-Integrios-Antiforgery",
    antiforgery_form_field_name: "__antiforgery",
  };

  function stubSignedIn() {
    stubHttp(({ url }) => ({
      status: 200,
      body: url.pathname === "/auth/session" ? session : page([]),
    }));
  }

  // A path parameter matches any segment, but these values are handed to the Admin API as
  // identifiers. The route table has to refuse a malformed one rather than pass it through.
  it("refuses a Tenant id that is not an identifier instead of passing it to the API", async () => {
    stubSignedIn();

    renderApp("/tenants/not-a-tenant/sources");

    expect(await screen.findByRole("heading", { name: "Not found" })).toBeTruthy();
  });

  it("refuses a nested id that is not an identifier", async () => {
    stubSignedIn();

    renderApp(`/tenants/${tenant}/topics/not-a-topic`);

    expect(await screen.findByRole("heading", { name: "Not found" })).toBeTruthy();
  });

  it.each([
    "/tenants/not-a-tenant/sources",
    `/tenants/${tenant}/topics/not-a-topic`,
    `/tenants/${tenant}/topics/${topic}/subscriptions/not-a-subscription`,
    "/connectors/not-a-connector",
  ])("never issues an Admin request for the refused route %s", async (path) => {
    const calls = stubHttp(({ url }) => ({
      status: 200,
      body: url.pathname === "/auth/session" ? session : page([]),
    }));

    renderApp(path);
    await screen.findByRole("heading", { name: "Not found" });

    expect(calls.filter((call) => call.url.pathname.startsWith("/admin"))).toEqual([]);
    vi.unstubAllGlobals();
  });

  it("keeps the root alias, query, fragment, and active Tenants destination", async () => {
    stubSignedIn();
    const { router } = renderApp("/?status=active#main");

    expect(await screen.findByRole("heading", { name: "Tenants" })).toBeTruthy();
    expect(router.state.location).toMatchObject({ pathname: "/", search: "?status=active", hash: "#main" });
    expect(
      within(screen.getByRole("navigation", { name: "Deployment" }))
        .getByRole("link", { name: "Tenants" })
        .getAttribute("aria-current"),
    ).toBe("page");
  });
});
