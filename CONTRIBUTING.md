# Contributing to Integrios

Thanks for your interest. Integrios is an early-stage, open-source integration platform;
contributions, issues, and feedback are welcome.

> **Maturity:** this is a preview release. The backend works end to end, including
> Operator-authored Connector definitions (see the [GitHub-to-Slack
> walkthrough](docs/github-to-slack-walkthrough.md) for a shipped, worked example), and Admin serves
> the Operator dashboard.

Every change follows [RULES.md](RULES.md).

## Getting set up

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker.

```bash
# Build the solution
dotnet build Integrios.slnx

# Run every test project (the functional and acceptance projects need Docker)
dotnet test Integrios.slnx

# Run one test project
dotnet test tests/Integrios.Ingestion.UnitTests/Integrios.Ingestion.UnitTests.csproj

# Run the full stack locally (services, Postgres, migrations)
make up

# Run one role from its own folder, so its development settings load
cd src/Integrios.Ingestion && dotnet run --project ../Integrios --launch-profile ingestion
cd src/Integrios.Admin && dotnet run --project ../Integrios --launch-profile admin
cd src/Integrios.Worker && dotnet run --project ../Integrios --launch-profile worker
cd src/Integrios.Subscriber && dotnet run --project ../Integrios --launch-profile subscriber

# Apply C# formatting (per .editorconfig)
dotnet format Integrios.slnx

# Inspect or apply database migrations through the Compose network
make db-info
make db-migrate
```

No `.env` is needed; the Makefile and `compose.yml` default all development values, and a repo-root
`.env` overrides them. See [docs/setup.md](docs/setup.md) for the end-to-end run and a first-event
walkthrough, and the [docs site guide](docs/ci-cd.md) to preview the public documentation.

For runtime proof, follow the [verification skill](.claude/skills/verify-integrios/SKILL.md).
Its `prove.sh` command launches an isolated stack and checks Event intake through HTTP delivery,
with retained evidence and nonzero exit on failure. Use the matching feature recipes for behavior
outside that smoke path. Run narrow tests while editing and prove affected user flows before
opening a PR; local proof supplements the shared CI gate.

## Project layout

- `Integrios.slnx` is the solution entry point.
- `src/Integrios` is the `integrios` executable that starts any role: `integrios ingestion`,
  `integrios admin`, `integrios worker`, or `integrios subscriber`. It is the only executable; the
  four projects below are host libraries it references, one per role.
- `src/Integrios.Ingestion` owns intake, Tenant authentication, and the durable acceptance boundary.
  Data plane, port 5231.
- `src/Integrios.Admin` owns Tenant management; Connector, Source, and Destination authoring; Topic
  and Subscription management; and it serves the Operator dashboard. Control plane, port 5150.
- `src/Integrios.Worker` owns outbox polling, fanout to Subscriptions, delivery, and retry and
  dead-letter behavior.
- `src/Integrios.Subscriber` owns pull delivery: Subscribers claim and settle deliveries for a
  pull Destination through it. Data plane, port 5241, loopback only by default.
- `src/Integrios.Application` holds the use cases: commands, queries, their handlers, and the ports
  they need, grouped by responsibility (such as `Authoring`, `Ingestion`, `Delivery`) and then by
  capability.
- `src/Integrios.Infrastructure` implements those ports: persistence, messaging, secret resolution,
  and other external-system adapters.
- `src/Integrios.Domain` holds entities, enums, and value objects.
- `src/Integrios.Client` is the unpublished .NET SDK preview for team-owned Subscriber hosts.
  It depends only on the Delivery API's HTTP protocol, with no server-project references.
- `src/Integrios.Migrations.Postgres/` and `src/Integrios.Migrations.SqlServer/` hold the
  provider-specific EF Core migrations.
- `tests/` holds the xUnit test projects, including `tests/Integrios.UnitTests` for the `integrios`
  executable; a project's name states what it needs to run.
- `docs/` holds the public documentation and is the source of the public site.
- The development Compose stack includes WireMock as a controllable local sink for testing and
  demos. It is not part of the deployable product.

## Choosing tests

A test project's name states what it needs to run. `Integrios.<Layer>.UnitTests` needs nothing;
`Integrios.FunctionalTests` starts databases and a message-broker emulator through Testcontainers;
`Integrios.AcceptanceTests` builds the image, composes the packaged deployment, and drives
the dashboard's golden journey against it in Chromium, so it also needs Node and `npm ci` plus
`npx playwright install chromium` in `src/Integrios.Admin/frontend`. Cost follows that ordering, and
acceptance dominates a run of everything.

Choose tests by what the change can actually break, not by default:

| Change | Run |
|---|---|
| frontend-only code under `src/Integrios.Admin/frontend/` | `npm --prefix src/Integrios.Admin/frontend run check` |
| backend or shared infrastructure code | `UnitTests` projects + `ArchitectureTests` |
| backend behavior in `src/` | add `FunctionalTests` on the default provider |
| raw SQL, JSON operators, migrations, or the `DbContext` | add the second provider leg, `INTEGRIOS_TEST_DATABASE_PROVIDER=sqlserver` |
| host composition, dependency registration, Dockerfile, Compose, bootstrap, or an HTTP contract the acceptance tests exercise | add `AcceptanceTests` |
| docs-only change | `npm run docs:build`, which fails on broken internal links |

The second provider leg is for plausible provider divergence, not reassurance: one provider catches
nearly everything that is not provider-specific. A schema or architecture change also checks that
migrations, tests, and docs stay aligned.

CI runs `AcceptanceTests` through the `acceptance` job on every non-PR run (`run_kind` `main`,
`nightly`, or `release`). Running it locally before each commit in a batch duplicates a gate that is
about to run anyway; run it once before pushing, or when the change is in the table above.

While iterating, filter to the narrowest relevant test rather than running a whole project:
`dotnet test <project> --filter "FullyQualifiedName~SourcesAdminTests"`. Do not partition a project
with traits or category filters. If you skip verification, say so explicitly.

Run narrow checks while editing, then the relevant checks above before pushing a coherent change.
Use the running app to prove behavior when the claim depends on runtime or browser interaction.
CI remains the shared merge gate; local results do not replace it. Avoid pushing each intermediate
edit just to obtain feedback from CI.

## Making changes

1. Fork the repo and branch off `main`.
2. Keep changes focused; add or update tests where you change behavior.
3. Run the tests [Choosing tests](#choosing-tests) names for your change before opening a PR.
4. Open a pull request against `main` with a clear description.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

```text
<type>(<scope>): <description>
```

Common types: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `perf`. Suggested scopes:
`admin`, `dashboard`, `ingestion`, `worker`, `api`, `core`, `db`, `infra`, `docs`.

```text
feat(api): add webhook intake endpoint with tenant resolution
fix(worker): handle null payload in delivery attempt tracker
docs: update domain model overview
chore(db): add initial migration for tenant and connector tables
```

## CI

Pull requests select whole CI jobs by changed paths. The `ci` check requires every selected job to
pass and accepts skips only for unselected jobs. Unknown paths select every PR check. Main, nightly,
and release runs keep the complete application checks, both database providers, and packaged
acceptance. Images are never published from a pull request.
[docs/ci-cd.md](docs/ci-cd.md) describes the selection rules and release gates.

After changing CI selection or its final gate, run:

```bash
python -m unittest discover -s .github/ci -v
```

## License

By contributing, you agree that your contributions are licensed under the project's
[MIT License](LICENSE).
