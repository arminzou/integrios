# Integrios verification map

This directory is the maintained source for verifying the user-facing behavior of Integrios. Read
this index before driving the app, then use the matching feature file as the recipe. Launch,
doctor, cleanup, and the helpers are described in [../SKILL.md](../SKILL.md); `$V` below means
`.claude/skills/verify-integrios/scripts`.

## Baseline preconditions

- A run started with `$V/control.sh up` (add `--dashboard` for browser features), and its env loaded
  with `source artifacts/verify/<run>/state/env`.
- `$V/control.sh doctor` passes.
- Seeded data comes from `eval "$($V/seed-delivery.sh)"`, which makes a new Tenant per call.
- Never drive an instance this run did not start, including a `make up` stack on the default ports.

## Driving conventions

- `bash $V/prove.sh` automates the Connector response, Event intake, successful HTTP delivery,
  idempotent resubmission, and rejected-credential checks on a fresh run. It does not cover the
  rest of this map. Use the feature files for change-specific proof.
- Start every recipe from the baseline. A recipe that needs a Tenant seeds its own.
- Prefer ARIA roles and accessible names in the browser. Form names come from the dashboard's own
  tests (`src/Integrios.Admin/frontend/tests/e2e/journey.browser.test.ts`).
- Use `curl -fsS` for setup calls so a refused write stops the recipe. Use
  `-sS -w '%{http_code}'` where the status code is the proof.
- Write each proof under `$EVIDENCE/<feature-id>/`.

## Proof and skip reporting

- Capture the action and the resulting state, then a second read-only view of what was stored.
- HTTP proof is the request, the status code, the response body, and the read-back.
- UI proof is an ARIA snapshot and a screenshot, plus the Admin API read-back of what the UI wrote.
- CLI proof is the command, stdout, stderr, and exit code.
- Report an unreachable path with the attempted command and the unmet precondition. Do not report a
  skipped entry point as verified through another one.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph of user-visible behavior, then four H2
sections in this order: `Sub-features`, `How to get to it (user POV)`, `Driving it with <harness>`,
and `Gotchas`.

## Features

- [Event intake](./event-intake.md) covers a Source posting an Event to Ingestion, idempotent
  resubmission, and rejected credentials.
- [HTTP delivery](./http-delivery.md) covers the Worker delivering an accepted Event to a
  Destination, failed attempts, dead-lettering, and replay.
- [Operator sign-in](./operator-sign-in.md) covers dashboard password sign-in, session restore,
  sign-out, and the OperatorKey on the Admin API.
- [Destinations](./destinations.md) covers creating, listing, changing the status of, and deleting
  a Destination in the dashboard and the Admin API.
- [Subscriptions](./subscriptions.md) covers creating, activating, and deactivating a Subscription
  in the dashboard and the Admin API.
- [Database CLI](./database-cli.md) covers `integrios admin database migrate`, `info`, and
  `grant-runtime`.

## Pending

- Pull delivery through the `delivery-api` role. That role is not on `main` yet. When it lands, add
  its launch step to `control.sh` and a feature file here.
