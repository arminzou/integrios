# CI/CD

Integrios ships an application workflow (`.github/workflows/ci.yml`) and a separate documentation
workflow (`.github/workflows/docs.yml`). The application flows keep routine feedback prompt while
still making complete verification mandatory before publishing a release:

1. **Pull request**: selects whole jobs by changed paths, then reports the combined `ci` gate.
   Selected application jobs retain locked restore, dependency audits, and complete test projects.
   No secrets are required, so this runs for fork pull requests too.
2. **Main**: runs all checks and both database providers, then the complete Acceptance project before
   publishing commit, `main`, and `latest` images.
3. **Nightly**: runs all checks, both database providers, and complete Acceptance on schedule.
4. **Release**: runs all checks, both database providers, and the complete Acceptance project before
   publishing images. A `v*` tag, or an explicit manual run from `main`, triggers it. See
   [Cutting a release](#cutting-a-release) for how that tag comes about.
5. **Deploy**: owned by you. Integrios publishes images; how you run them (Compose,
   Kubernetes, etc.) lives in your own infrastructure, not in this repo.

Documentation-only pull requests select the documentation job in `ci.yml`. The separate `docs.yml`
workflow publishes documentation changes from `main`. Each documentation build installs
the root lockfile with `npm ci`, builds the VitePress site, checks representative output pages,
and scans the generated files for private content markers. The build fails on broken internal links.
Pull requests only check the build; successful `main` runs publish it to
[GitHub Pages](https://arminzou.github.io/integrios/). Documentation-only PRs do not start application
checks; pushes to main still receive comprehensive verification. For local documentation work, run `npm ci`, `npm run docs:dev` to preview, and
`npm run docs:build` to check the production build and internal links.

## Pull-request selection

`.github/ci/checks.py` owns job selection. Changes combine their required jobs; new or unclassified
paths select all PR checks. The selector compares the PR merge commit with the event's base commit,
including both sides of a rename and deleted paths. A failed diff blocks the final gate.

| Changed area | Selected PR jobs |
| --- | --- |
| Verification skill under `.claude/skills/verify-integrios/` | Helper checks |
| Markdown, `docs/`, or `LICENSE` | Helper checks and docs build |
| `src/Integrios.Admin/frontend/` | Helper checks and full frontend checks, including API generation |
| Admin or Application C# | Helper checks, architecture/unit checks, frontend, PostgreSQL functional |
| Ingestion or Worker C# | Helper checks, architecture/unit checks, PostgreSQL functional |
| Infrastructure, Domain, migrations, or C# tests | Helper checks, architecture/unit checks, frontend, both functional providers |
| Host entry point, project/dependency files, runtime settings, deployment, CI, build tooling, or unknown paths | All PR jobs |

Frontend dependency files stay in the frontend job. Other project/dependency files and runtime
settings select all jobs. Helper checks always test the selector and final gate; when the
verification skill is present, they also run its control-helper regression checks.

The application jobs start independently after selection. The functional matrix contains PostgreSQL
and adds SQL Server for provider-sensitive changes. Tests within each project are not filtered.
Release-please's same-repository PR selects docs and helpers only when every changed file is a
known release-metadata output. Additional files follow normal selection. Main and tag runs still
perform comprehensive verification.

A new push cancels older runs for the same PR. Main, nightly, manual, and tag runs have separate
concurrency groups and do not cancel one another. Acceptance waits for `ci`; publishing waits for
both `ci` and acceptance.

Configure the main branch ruleset to require the GitHub Actions check named `ci`. It fails when
selection fails, a selected job fails or is cancelled, or a selected job is unexpectedly skipped.
The gate runs even for documentation-only PRs, so path selection cannot leave a required check
permanently pending. Do not require individual conditional jobs.

Use narrow local tests during iteration and exercise affected user flows when runtime evidence is
needed. Push a coherent change, then use CI as the merge gate. A passing local run does not waive
CI, and slow or flaky tests must be fixed rather than silently omitted.

## Cutting a release

Versions are derived from Conventional Commits, not chosen by hand.
`.github/workflows/release-please.yml` keeps a release pull request open against `main`. Every
commit that lands updates it: the next version in `VERSION` and `deploy/compose.yml`, and the
release notes in `CHANGELOG.md`. A `feat` makes it a minor, a `fix` a patch; while the major is
`0`, a breaking change is a minor too.

To release, merge that pull request. Release-please tags the merge commit and creates the GitHub
release, and that tag starts the release run above.

Merging is the release decision. Nothing publishes while the pull request sits open, so leaving it
open until a body of work is complete costs nothing.

The `Integrios.Client` SDK has its own version line. Release-please opens a separate pull request
for commits under `src/Integrios.Client` and tags its merge `client-v<version>`, which starts
`.github/workflows/client-release.yml`: it tests and packs the SDK and pushes it to
[nuget.org](https://www.nuget.org/packages/Integrios.Client). nuget.org is the SDK's only channel.
Its GitHub release carries no files, and the workflow hands GitHub's Latest badge back to the
newest server release, so the Releases page always leads with the server.

A client release that needs a server change ships with or after the server release that has it.
The SDK's README states the minimum server version, and its changelog notes each change to it.

## What the default pipeline does

The main and release flows publish one image to GitHub Container Registry (GHCR) under:

```
ghcr.io/<owner>/<repo>
```

Main images are tagged with the commit SHA and `main`. Release images are tagged with the commit
SHA, the version and its `<major>.<minor>`, and `latest`, so `latest` always means the newest
stable release. Each image includes an SBOM and build provenance attestation.

The image holds all three roles. Its entrypoint is `/app/integrios`, and each container names its
role as the first argument: `ingestion`, `admin`, or `worker`. Each role still composes only its
own capabilities — Worker resolves delivery secrets and Admin never does. The image is built from
a single repo-root `Dockerfile`, so the base image pins and the Npgsql GSSAPI workaround exist in
exactly one place.

**The roles are a matched set.** A given version tag assumes all three roles run that same
version against a database migrated to the schema they expect. Nothing at runtime
prevents mixing versions, and a release that changes the schema will misbehave if only some
services are rolled forward. Deploy Ingestion, Admin, and Worker together, from the same tag or
digest, and run migrations before starting them. The deployment reference in
`deploy/compose.yml` enforces this by running every role from one image at a single
`INTEGRIOS_VERSION`; keep that property if you adapt it.

This coordinated rollout is required for Destination authentication capabilities such as
`oauth2_client_credentials`: Admin must not author a scheme that the running Worker cannot execute.
Before rolling back to a version without that scheme, drain active OAuth EventDeliveries or accept
that their snapshotted authentication is unexecutable and will follow the normal dead-letter path.

Successful release runs retain a downloadable evidence artifact for 90 days. It contains
the workflow and commit identity, .NET and dependency versions, exact verification
commands, test logs and TRX results, resolved external image digests, and the published image
digest.

Each release also attaches `integrios-<version>-win-x64.zip`, a self-contained Windows build
cross-published on the Linux runner, and `checksums.txt` with the SHA-256 of every attached file.
Both are attached only after the image is published. A rerun keeps an archive that is already
attached and regenerates `checksums.txt` from it, because a rebuilt archive is not byte-identical.
The evidence records the SHA-256 and build provenance attestation of the archive that run built,
under `windows-archive/`, so a rerun's evidence can describe a build that was not attached.

The release notes open with an Install section: the image pinned by digest, a `docker pull`
command, and links to `deploy/compose.yml` and `deploy/azure/` at the release tag. Rerunning the
release replaces that section rather than adding another.

The repository pins its .NET SDK, NuGet dependency graph, container versions, and
GitHub Actions. Action references use release-version tags such as `@v7.0.1`;
keep that form when adapting the workflow.

Publishing exists only in the main and release flows. Pull requests, including those from
forks, run only the read-only verification job and never need or receive registry
credentials.

## Publishing to your own registry

The workflow lives in one file you own in your fork. To publish elsewhere, edit the
`package` job in `ci.yml`:

- **Different GHCR namespace**: nothing to change. `images:` uses
  <code v-pre>ghcr.io/${{ github.repository }}</code>, so your fork publishes under your own
  owner/repo automatically.
- **A non-GHCR registry** (Docker Hub, a private registry, etc.): point the `Log in`
  step and the `images:` value at your registry, and supply credentials as repository
  secrets:

  ```yaml
  - name: Log in
    uses: docker/login-action@c94ce9fb468520275223c153574b00df6fe4bcc9 # v3.7.0
    with:
      registry: registry.example.com
      username: ${{ secrets.REGISTRY_USERNAME }}
      password: ${{ secrets.REGISTRY_PASSWORD }}

  - name: Derive image metadata
    id: meta
    uses: docker/metadata-action@c299e40c65443455700f0fdfc63efafe5b349051 # v5.10.0
    with:
      images: registry.example.com/my-team/integrios
  ```

- **Multi-arch images** (e.g. ARM hosts): add `platforms: linux/amd64,linux/arm64` to
  the `Build and push` step.

## Consuming the image

Pull the published image by tag or digest from your registry and run it with your own
orchestration. The repository's `compose.yml` is a working reference for how the services,
database, and migrations fit together; adapt it (or translate it to your platform) for
your environment.
