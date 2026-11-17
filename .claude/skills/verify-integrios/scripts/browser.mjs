#!/usr/bin/env node
// Signs in to the run's dashboard with the run's Operator password, opens a route, and writes an
// ARIA snapshot and a screenshot of it. Requires the run's env (source artifacts/verify/<run>/state/env).
//   node browser.mjs <route-without-leading-slash> <evidence-prefix>
//   node browser.mjs tenants "$EVIDENCE/dashboard/tenants"
// Git Bash rewrites a leading-slash argument into a Windows path, so routes start without one.
// Import signIn() from a scratch script for multi-step journeys.
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const frontend = resolve(here, "../../../../src/Integrios.Admin/frontend/package.json");
const { chromium } = createRequire(frontend)("playwright");

export async function signIn(browser) {
  const { ADMIN, OPERATOR_EMAIL, OPERATOR_PASSWORD_FILE } = process.env;
  if (!ADMIN || !OPERATOR_PASSWORD_FILE) throw new Error("Source the run env first.");
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await page.goto(ADMIN);
  await page.getByLabel("Email", { exact: true }).fill(OPERATOR_EMAIL);
  await page.getByLabel("Password", { exact: true }).fill(await readFile(OPERATOR_PASSWORD_FILE, "utf8"));
  const session = page.waitForResponse((r) => new URL(r.url()).pathname === "/auth/session" && r.status() === 200);
  await page.getByRole("button", { name: "Sign in with email", exact: true }).click();
  await session;
  return page;
}

export async function capture(page, prefix) {
  await mkdir(dirname(prefix), { recursive: true });
  await writeFile(`${prefix}.aria.txt`, await page.locator("body").ariaSnapshot());
  await page.screenshot({ path: `${prefix}.png`, fullPage: true });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [route, prefix] = process.argv.slice(2);
  if (!route || !prefix) {
    console.error("usage: node browser.mjs <route> <evidence-prefix>");
    process.exit(2);
  }
  const browser = await chromium.launch();
  try {
    const page = await signIn(browser);
    await page.goto(new URL(route, `${process.env.ADMIN}/`).href);
    await page.getByRole("heading", { level: 1 }).waitFor();
    await capture(page, prefix);
    console.log(`captured ${page.url()} -> ${prefix}.{aria.txt,png}`);
  } finally {
    await browser.close();
  }
}
