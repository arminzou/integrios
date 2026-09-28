// @vitest-environment node

import { readFile } from "node:fs/promises";
import { chromium } from "playwright";
import { describe, expect, it } from "vitest";

// The Acceptance harness creates this disposable account through the packaged operator-user CLI.
// This journey uses the built dashboard and real cookie/antiforgery endpoints, with no routes mocked.
const origin = process.env.INTEGRIOS_JOURNEY_ORIGIN;
const passwordFile = process.env.INTEGRIOS_JOURNEY_PASSWORD_FILE;

describe.skipIf(!origin || !passwordFile)("Packaged Operator password login", () => {
  it("signs in without OIDC and uses the resulting browser session", async () => {
    const browser = await chromium.launch();
    try {
      const page = await browser.newPage();
      // Read the dashboard's own options response. A second request here would race it without a
      // cookie, receive a different antiforgery cookie, and break the token pairing on slow links.
      const optionsResponse = page.waitForResponse((response) => new URL(response.url()).pathname === "/auth/options");
      await page.goto(origin!);
      const options = await optionsResponse;
      expect(options.status()).toBe(200);
      expect(await options.json()).toMatchObject({ password_enabled: true, oidc_enabled: false });
      await page.getByLabel("Email", { exact: true }).fill("operator@example.test");
      await page.getByLabel("Password", { exact: true }).fill(await readFile(passwordFile!, "utf8"));
      const signedIn = page.waitForResponse(
        (response) => new URL(response.url()).pathname === "/auth/session" && response.status() === 200,
      );
      await page.getByRole("button", { name: "Sign in with email", exact: true }).click();
      const session = await signedIn;
      expect(await session.json()).toMatchObject({ display_name: "Acceptance Operator" });
      // APIRequestContext shares this page's cookies and supplies no OperatorKey.
      const tenants = await page.request.get(`${origin}/admin/tenants`);
      expect(tenants.status()).toBe(200);
      const restored = page.waitForResponse(
        (response) => new URL(response.url()).pathname === "/auth/session" && response.status() === 200,
      );
      await page.reload();
      expect((await restored).status()).toBe(200);
      expect(await page.getByRole("form", { name: "Sign in with email and password" }).count()).toBe(0);
      expect((await page.context().cookies()).some((cookie) => cookie.httpOnly)).toBe(true);
    } finally {
      await browser.close();
    }
  }, 60_000);
});
