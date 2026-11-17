# Operator sign-in

An Operator signs in to the dashboard with an email and a password created by
`integrios admin operator-user create`, keeps the session across a reload, and signs out. Machines
use the OperatorKey on the Admin API instead.

## Sub-features

- `signin-password` signs in through the dashboard's email-and-password form.
- `signin-restore` restores the session after a reload without showing the sign-in form.
- `signin-signout` ends the session with the `Sign out` button.
- `signin-operator-key` authorizes Admin API calls with `Authorization: OperatorKey ...`.

## How to get to it (user POV)

- Open `$ADMIN` in a browser. The sign-in form is named `Sign in with email and password`.
- Call the Admin API with the `$AUTH` header.

## Driving it with browser.mjs and curl

Preconditions:

- The run was started with `up --dashboard`, `doctor` passes, and Chromium is installed for
  Playwright.
- The env provides `OPERATOR_EMAIL` and `OPERATOR_PASSWORD_FILE`.

- **Sign in.** Run `node $V/browser.mjs tenants $EVIDENCE/operator-sign-in/tenants`. The helper fills `Email` and `Password`, chooses `Sign in with email`, and waits for `/auth/session` to answer `200`. The snapshot shows the `Tenants` heading, and `Verification Operator` next to `Sign out`.
- **Restore.** In a scratch script that imports `signIn` from `$V/browser.mjs`, call `page.reload()` and wait for `/auth/session` to answer `200`. `page.getByRole("form", { name: "Sign in with email and password" })` has count `0`.
- **Sign out.** Choose `page.getByRole("button", { name: "Sign out" })`. The sign-in form returns, and `GET $ADMIN/admin/tenants` from the same page context answers `401`.
- **OperatorKey.** Run `curl -sS -o /dev/null -w '%{http_code}' $ADMIN/admin/tenants -H "$AUTH"` and expect `200`. The same call without the header answers `401`.

## Gotchas

- Read `/auth/options` from the page's own load. A second request without the page's cookie gets a
  different antiforgery cookie and breaks the token pairing.
- Password sign-in needs `Integrios:Admin:Password:Enabled=true`, which `appsettings.admin.json`
  sets. `doctor` checks that `/auth/options` reports it.
- Passwords must have at least 15 characters. `--password-file` reads the file verbatim, including
  a trailing newline, so write it with `printf %s`.
- The dashboard is served only after `up --dashboard` (or `npm run build:assets`).
