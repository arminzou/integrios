import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { OperatorAuthenticationOptions } from "./api/client";
import { expectNoAccessibilityViolations } from "./test/axe";
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

const authOptions: OperatorAuthenticationOptions = {
  oidc_enabled: true,
  password_enabled: true,
  oidc_display_name: "Example ID",
  antiforgery_token: "sign-in-token",
  antiforgery_header_name: "X-Integrios-Antiforgery",
};

describe("Application session", () => {
  function stubSignedOut(options = authOptions) {
    return stubHttp(({ url }) => (url.pathname === "/auth/session" ? { status: 401 } : { status: 200, body: options }));
  }

  it("offers anonymous Operators a sign-in that returns to the current local route", async () => {
    // `signInHref` reads the document's own location, which is what a real sign-in round trip
    // returns to; the memory router only decides which screen is behind the anonymous shell.
    history.replaceState(null, "", "/tenants?status=active");
    stubSignedOut();

    renderApp("/tenants");

    expect((await screen.findByRole("link", { name: "Continue with Example ID" })).getAttribute("href")).toBe(
      "/auth/login?return_to=%2Ftenants%3Fstatus%3Dactive",
    );
    expect(screen.getByRole("heading", { name: "Sign in to Integrios" })).toBeTruthy();
    expect(screen.getByText("You will be returned to the page you were on.")).toBeTruthy();
  });

  it.each([
    ["/?signed_out=1", "You are signed out.", "Your identity provider session was left as it was."],
    ["/?error=access_denied", "Sign-in did not complete.", "Your identity provider refused this sign-in."],
  ])("renders the anonymous state carried by %s", async (path, state, message) => {
    history.replaceState(null, "", path);
    stubSignedOut();

    renderApp("/");

    expect(await screen.findByRole("heading", { name: "Sign in to Integrios" })).toBeTruthy();
    expect(await screen.findByText(state)).toBeTruthy();
    expect(screen.getByRole("link", { name: "Continue with Example ID" })).toBeTruthy();
    expect(screen.getByText(message, { exact: false })).toBeTruthy();
  });

  it("puts configured OIDC first and keeps the password failure generic", async () => {
    const path = "/tenants/22222222-2222-2222-2222-222222222222/events?status=dead_lettered";
    history.replaceState(null, "", path);
    const calls = stubHttp(({ url }) => {
      if (url.pathname === "/auth/session") return { status: 401 };
      if (url.pathname === "/auth/options") return { status: 200, body: authOptions };
      return { status: 401, body: { message: "Email or password is invalid." } };
    });

    const { container } = renderApp(path);

    const oidc = await screen.findByRole("link", { name: "Continue with Example ID" });
    const password = screen.getByRole("button", { name: "Sign in with email" });
    expect(oidc.compareDocumentPosition(password) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.getByText("or")).toBeTruthy();
    expect(screen.queryByRole("link", { name: /forgot/i })).toBeNull();
    await expectNoAccessibilityViolations(container);

    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "correct horse battery staple" } });
    fireEvent.submit(screen.getByRole("form", { name: "Sign in with email and password" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Email or password is invalid.");
    const request = calls.find(({ url }) => url.pathname === "/auth/password/login")!;
    expect(request.body).toEqual({
      email: "operator@example.test",
      password: "correct horse battery staple",
      return_to: path,
    });
    expect(request.headers.get("X-Integrios-Antiforgery")).toBe("sign-in-token");
  });

  it("shows only OIDC when password sign-in is disabled", async () => {
    stubSignedOut({ ...authOptions, password_enabled: false });

    renderApp("/");

    expect(await screen.findByRole("link", { name: "Continue with Example ID" })).toBeTruthy();
    expect(screen.queryByLabelText("Email")).toBeNull();
  });

  it("shows only email and password when OIDC is disabled", async () => {
    stubSignedOut({ ...authOptions, oidc_enabled: false, oidc_display_name: null });

    renderApp("/");

    expect(await screen.findByRole("button", { name: "Sign in with email" })).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Continue with Example ID" })).toBeNull();
  });

  it("reports and retries a failed session bootstrap", async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(new Response(null, { status: 503 }))
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(Response.json(authOptions));
    vi.stubGlobal("fetch", fetch);

    renderApp("/tenants");

    expect(await screen.findByText("The session could not be read (503).", { selector: "strong" })).toBeTruthy();
    screen.getByRole("button", { name: "Retry" }).click();
    expect(await screen.findByRole("link", { name: "Continue with Example ID" })).toBeTruthy();
    expect(fetch).toHaveBeenCalledTimes(3);
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

  it("stops nested screens when the route Tenant does not exist", async () => {
    const calls = stubHttp(({ url }) => {
      if (url.pathname === "/auth/session") return { status: 200, body: session };
      if (/^\/admin\/tenants\/[^/]+$/.test(url.pathname)) return { status: 404, body: { title: "Not Found" } };
      return { status: 200, body: page([]) };
    });

    renderApp("/tenants/22222222-2222-2222-2222-222222222222/sources");

    expect(await screen.findByRole("heading", { name: "Tenant not found" })).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toContain("This Tenant does not exist");
    expect(screen.getByRole("link", { name: "Go to Tenants" }).getAttribute("href")).toBe("/tenants");
    expect(screen.queryByRole("button", { name: "New Source" })).toBeNull();
    expect(calls.some(({ url }) => url.pathname.endsWith("/sources"))).toBe(false);
  });

  it("orders the Tenant sections in authoring sequence, grouped Author and Observe", async () => {
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
    const names = within(tenantNav)
      .getAllByRole("link")
      .map((link) => link.textContent);
    const at = (name: string) => names.indexOf(name);

    // The authoring sequence: a Source publishes through a Topic to a Destination, delivered by a
    // Subscription; Events is the observe run, and API keys sit last.
    expect(at("Overview")).toBeLessThan(at("Sources"));
    expect(at("Sources")).toBeLessThan(at("Topics"));
    expect(at("Topics")).toBeLessThan(at("Destinations"));
    expect(at("Destinations")).toBeLessThan(at("Subscriptions"));
    expect(at("Subscriptions")).toBeLessThan(at("Events"));
    expect(at("Events")).toBeLessThan(at("API keys"));

    // The two runs carry their own label, so the grouping is not only visual.
    expect(within(tenantNav).getByText("Author")).toBeTruthy();
    expect(within(tenantNav).getByText("Observe")).toBeTruthy();
  });
});
