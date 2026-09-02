# Contributing to Integrios

Thanks for your interest. Integrios is an early-stage, open-source integration platform;
contributions, issues, and feedback are welcome.

> **Maturity:** this is a preview release. The backend foundation works end to end, including
> Operator-authored Connector definitions (see the [GitHub-to-Slack
> walkthrough](docs/github-to-slack-walkthrough.md) for a shipped, worked example); an Operator
> admin UI is planned and not yet implemented — every capability is driven through the Admin and
> Ingestion HTTP APIs today.

## Getting set up

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker.

```bash
# Build the solution
dotnet build Integrios.slnx

# Run the unit tests
dotnet test Integrios.slnx

# Run the full stack locally (services, Postgres, migrations)
make up
```

See [docs/setup.md](docs/setup.md) for the end-to-end run and a first-event walkthrough.

## Project layout

- `src/` contains the services: `Integrios.Ingestion` (data plane), `Integrios.Admin` (control
  plane), `Integrios.Worker` (delivery), plus `Integrios.Domain`, `Integrios.Application`,
  `Integrios.Infrastructure`, plus WireMock as the bundled local test sink.
- `tests/`: xUnit test projects.
- `src/Integrios.Migrations.Postgres/` and `src/Integrios.Migrations.SqlServer/`: provider-specific
  EF Core migrations.
- `docs/`: public documentation.

## Making changes

1. Fork the repo and branch off `main`.
2. Keep changes focused; add or update tests where you change behavior.
3. Run `dotnet build` and the relevant `dotnet test` projects before opening a PR.
4. Open a pull request against `main` with a clear description.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <description>
```

Common types: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `perf`. Suggested
scopes: `api`, `admin`, `worker`, `core`, `db`, `docs`, `infra`.

## CI

Every pull request runs build + unit tests via GitHub Actions. Image publishing runs only
on the canonical repository, never on fork PRs.

## License

By contributing, you agree that your contributions are licensed under the project's
[MIT License](LICENSE).
