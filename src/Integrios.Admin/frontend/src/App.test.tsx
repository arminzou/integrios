import { cleanup, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { page, stubHttp } from "./test/http";
import { renderApp } from "./test/router";

afterEach(() => {
  cleanup();
  history.replaceState(null, "", "/");
});

const session = {
  user_id: "11111111-1111-1111-1111-111111111111",
  display_name: "Operator",
  email: "operator@example.test",
  antiforgery_token: "test-token",
  antiforgery_header_name: "X-Integrios-Antiforgery",
  antiforgery_form_field_name: "__antiforgery",
};

describe("Application session", () => {
  it("offers anonymous Operators a sign-in that returns to the current local route", async () => {
    // `signInHref` reads the document's own location, which is what a real sign-in round trip
    // returns to; the memory router only decides which screen is behind the anonymous shell.
    history.replaceState(null, "", "/tenants?status=active");
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(null, { status: 401 })),
    );

    renderApp("/tenants");

    expect((await screen.findByRole("link", { name: "Sign in" })).getAttribute("href")).toBe(
      "/auth/login?return_to=%2Ftenants%3Fstatus%3Dactive",
    );
  });

  it("reports a failed session bootstrap", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(null, { status: 503 })),
    );

    renderApp("/tenants");

    expect((await screen.findByRole("alert")).textContent).toBe("The session could not be read (503).");
  });

  it("renders the signed-in shell, unknown route, and server-named logout token", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => Response.json(session, { headers: { "content-type": "application/json" } })),
    );

    renderApp("/not-owned");

    await screen.findByText("Operator", { selector: "strong" });
    expect(screen.getByRole("heading", { name: "Not found" })).toBeTruthy();

    const form = screen.getByRole("button", { name: "Sign out" }).closest("form")!;
    const token = form.querySelector("input[type=hidden]") as HTMLInputElement;
    expect(form.getAttribute("action")).toBe("/auth/logout");
    expect(token.name).toBe(session.antiforgery_form_field_name);
    expect(token.value).toBe(session.antiforgery_token);
  });
});

describe("The signed-in rail", () => {
  function stubSignedIn(tenant?: Record<string, unknown>) {
    stubHttp(({ url }) => {
      if (url.pathname === "/auth/session") return { status: 200, body: session };
      if (/^\/admin\/tenants\/[^/]+$/.test(url.pathname) && tenant) return { status: 200, body: tenant };
      return { status: 200, body: page([]) };
    });
  }

  // The Tenant group is the rail saying which scope is open. On a deployment-wide route no Tenant is
  // open, so the group is absent rather than present and empty — which is the thing a horizontal row
  // could not express and the reason this shell changed.
  it("omits the Tenant group entirely when no Tenant is open", async () => {
    stubSignedIn();

    renderApp("/connectors");

    expect(await screen.findByRole("navigation", { name: "Deployment" })).toBeTruthy();
    expect(screen.queryByRole("navigation", { name: "Tenant" })).toBeNull();
  });

  it("names the open Tenant in its own group, and links back to the list to change it", async () => {
    stubSignedIn({
      id: "22222222-2222-2222-2222-222222222222",
      slug: "acme",
      name: "Acme",
      status: "active",
      environment: "production",
      description: null,
      created_at: "2026-09-01T00:00:00Z",
      updated_at: "2026-09-01T00:00:00Z",
    });

    renderApp("/tenants/22222222-2222-2222-2222-222222222222/topics");

    const tenantNav = await screen.findByRole("navigation", { name: "Tenant" });
    const switcher = await within(tenantNav).findByRole("link", { name: /Acme/ });
    expect(switcher.getAttribute("href")).toBe("/tenants");
    expect(within(tenantNav).getByRole("link", { name: "Topics" }).getAttribute("aria-current")).toBe("page");

    // Sign out stays a native form post, not a link the rail could turn into a navigation.
    expect(screen.getByRole("button", { name: "Sign out" }).closest("form")?.getAttribute("action")).toBe(
      "/auth/logout",
    );
  });
});
