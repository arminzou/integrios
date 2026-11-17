---
name: verify-integrios
description: Launches an isolated Integrios stack (PostgreSQL and a WireMock sink in Compose; Admin, Ingestion, and Worker built from this checkout) and drives it the way an Operator or a Source does — Event intake, HTTP delivery, the Admin API and Operator dashboard, and the `integrios admin database` CLI — then captures evidence. Use to prove a change works in the running app rather than only in tests.
---

# Verify Integrios

Drive the real app built from this checkout. Do not rerun suites CI already runs; spend the time on
the running app. Read [features/README.md](features/README.md) for the map, then the feature file
for what you are proving.

Every command below runs from the repository root in Bash (Git Bash on Windows). The helpers live in
`.claude/skills/verify-integrios/scripts/`; this file calls that directory `$V`:

```bash
V=.claude/skills/verify-integrios/scripts
```

## Launch

```bash
$V/control.sh up               # API-only: Event intake, delivery, Admin API, CLI
$V/control.sh up --dashboard   # also builds the dashboard into src/Integrios.Admin/wwwroot
source artifacts/verify/default/state/env
```

`up` starts from an empty database every time and takes about 30 seconds, plus about 40 seconds
with `--dashboard` on a cold `npm ci`. It:

1. Refuses to start if the run's state directory exists or any of its ports is taken.
2. Starts `postgres` and `mocksink` from `compose.yml` under the Compose project
   `integrios-verify-<run>`, with its own volume, and waits for them to be healthy.
3. Builds `src/Integrios` with `dotnet build` and records the commit only after it succeeds.
   There is no build-skip option: an older binary must not pass the revision check.
4. With `--dashboard`, runs `npm ci` (only when `node_modules` is missing) and `npm run build:assets`
   in `src/Integrios.Admin/frontend`.
5. Runs the same bootstrap the Compose stack runs (see `docs/setup.md`): `database migrate` as the
   owner, `database grant-runtime` for `admin_runtime` and `data_runtime`, `bootstrap` for the
   OperatorKey `global_operator_key:operator_bootstrap_secret`, and `operator-user create
   --password-file` for `operator@example.test` with a random password stored in the state
   directory.
6. Starts `admin`, `ingestion`, and `worker` as background `dotnet integrios.dll <role>` processes,
   each under its runtime database principal, and waits until all three `/ready` endpoints answer
   `200`. Ready means the role reached the database, not only that the port listens.

The loaded env exports `ADMIN`, `INGESTION`, `MOCKSINK`, `ADMIN_OPS`, `INGESTION_OPS`,
`WORKER_OPS`, `AUTH` (an `Authorization: OperatorKey ...` header), `OPERATOR_EMAIL`,
`OPERATOR_PASSWORD_FILE`, and `EVIDENCE`.

### Ports and parallel runs

Ports are the Compose defaults plus `VERIFY_PORT_OFFSET` (default `20000`), so they never collide
with a `make up` stack:

| Service | Port with default offset |
| --- | --- |
| PostgreSQL | 25432 |
| mocksink (WireMock) | 25054 |
| Admin API and dashboard | 25150 (operational 25151) |
| Ingestion | 25231 (operational 25232) |
| Worker operational | 25299 |

Other worktrees may run Testcontainers or their own verification at the same time. For a second
concurrent run, set both variables for every `control.sh` call of that run:

```bash
VERIFY_RUN=pr42 VERIFY_PORT_OFFSET=21000 $V/control.sh up
source artifacts/verify/pr42/state/env
```

Only one build may run in a worktree at a time; parallel `dotnet build` or `generate:api` runs
collide in `obj/`. Use a separate worktree for concurrent runs so each has its own binaries.

### One role by hand

To iterate on one role while the rest of the run stays up, stop that role's process (its PID is in
`artifacts/verify/<run>/state/<role>.pid`) and run it in the foreground with the same settings:

```bash
ConnectionStrings__Postgres='Host=127.0.0.1;Port=25432;Database=integrios;Username=data_runtime;Password=data_runtime_dev' \
DOTNET_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:25231 OperationalPort=25232 \
dotnet run --no-launch-profile --project src/Integrios -- ingestion
```

`--no-launch-profile` keeps `launchSettings.json` from forcing the default ports. Admin uses
`admin_runtime` / `admin_runtime_dev` and also needs
`Integrios__PublicIngestionBaseUri=http://127.0.0.1:25231` and, to serve the dashboard, the
`src/Integrios.Admin` working directory. The Worker takes no `ASPNETCORE_URLS`.

### Windows notes

- Use `Host=127.0.0.1` in every connection string, never `localhost`. Measured on Docker Desktop
  29.6.2: `database info` took 8.6–9.0 seconds through `localhost` and 1.0–1.9 seconds through
  `127.0.0.1`. The extra time on a cold connection outlasts the 5-second database readiness check,
  so `/ready` can stall.
- Git Bash rewrites an argument that starts with `/` into a Windows path. Pass dashboard routes to
  `browser.mjs` without the leading slash, and quote URLs in full.

## Doctor

Run it first, and again whenever anything looks off. It is read-only and exits non-zero on any
failure.

```bash
$V/control.sh doctor
```

It checks that the three role processes from this run are alive, each `/ready` answers, the
OperatorKey is accepted by `GET /admin/tenants`, password sign-in is enabled, mocksink answers,
the PostgreSQL container belongs to this run's Compose project, and the run was built from the
current `HEAD`. It also reports whether dashboard assets exist. A `built from current HEAD` failure
means you changed commits since `up`: run `down` and `up` again.

## Drive

- **HTTP:** `curl` with `$AUTH` for the Admin API, a TenantApiKey bearer token for Ingestion, and
  WireMock's `/__admin/requests/find` on `$MOCKSINK` for what the Worker delivered.
- **Seed data:** `eval "$($V/seed-delivery.sh)"` creates a fresh Tenant wired end to end (the `http`
  example Connector, a TenantApiKey, a Destination at `$MOCKSINK/sink/<slug>`, a `payments` Topic,
  an active Event API Source and an active Subscription for `payment.created`) and exports `SLUG`,
  `TENANT`, `TOKEN`, `CONNECTOR`, `DESTINATION`, `TOPIC`, `SOURCE`, and `SUBSCRIPTION`. Each call
  uses a new slug, so it never collides with earlier seeds in the same run.
- **Browser:** `node $V/browser.mjs <route> <evidence-prefix>` signs in through the real password
  form, opens the route, and writes `<prefix>.aria.txt` and `<prefix>.png`. For multi-step journeys,
  import `signIn` and `capture` from it in a scratch `.mjs` file; the selectors in each feature file
  come from the dashboard's own tests. It needs `up --dashboard` and, once per machine,
  `npx playwright install chromium` in `src/Integrios.Admin/frontend`.
- **CLI:** `$V/control.sh integrios <role> <args...>` runs the built binary against this run's
  database, as the owner by default. Prefix `ConnectionStrings__Postgres=...` to run as a runtime
  principal.

## Evidence

Write proof under `$EVIDENCE/<feature>/` (that is, `artifacts/verify/<run>/evidence/`, which Git
ignores). Cleanup keeps it.

- Use the real user path: the Ingestion endpoint a Source calls, the Admin API or dashboard an
  Operator uses, the CLI an operator runs. Never write rows directly or call test-only endpoints.
- Capture the action and the resulting state: the request and its status code, then a second
  read-only view of the stored result, such as `GET /events/{id}` on Ingestion or the Admin
  deliveries list.
- Verify side effects: the sink journal entry for a delivery, the row the dashboard created as read
  back through the Admin API, the CLI's exit code and output.
- mocksink is the only mock. It stands in for the downstream HTTP system, which the Destination
  boundary already isolates.

## Cleanup

```bash
$V/control.sh down
```

It signals the three role processes by the PIDs this run recorded and waits up to 75 seconds per
role for exit before removing the database. If a role stays alive, cleanup fails and keeps the
database and state for a later `down` attempt. On success, it removes the run's Compose
containers and volume, and deletes `artifacts/verify/<run>/state`. It never kills by process name
and never touches `artifacts/verify/<run>/evidence`. Run it after every failed attempt too, so a
stale run does not hold ports. If `up` failed after starting roles, `down` still stops them. Built
dashboard assets in `src/Integrios.Admin/wwwroot` stay; they are build output that Git ignores.

## Proven

The skill was proven end to end on Windows 11 with Git Bash and Docker Desktop: `up --dashboard`,
`doctor`, the event-delivery feature, a dashboard capture, `down`, and evidence kept after
cleanup. A Linux run has not been made yet. Its unverified points are stopping roles with `kill`
on their PIDs and the `/dev/tcp` port probe in `control.sh`.

## Maintenance

Run `bash $V/control.test.sh` after changing the control helper. It checks environment quoting,
build-before-revision ordering, and cleanup ordering and timeout without launching the stack.

Keep the feature map honest as the app changes with `/maintain-verification-skill`.
