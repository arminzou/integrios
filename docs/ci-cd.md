# CI/CD

Integrios ships one tiered GitHub Actions workflow (`.github/workflows/ci.yml`) you can run
as-is or fork and adapt. The tiers keep routine feedback prompt while still making complete
qualification mandatory before publishing a release:

1. **Pull request**: locked restore, dependency audit, Release build, architecture and component
   tests, plus the same Functional suite against PostgreSQL and SQL Server 2022. No configuration or
   secrets are required, so this runs for fork pull requests too.
2. **Main**: repeats the pull-request gate, then runs the complete Acceptance project before
   publishing commit, `main`, and `latest` images.
3. **Nightly**: repeats the pull-request gate and complete Acceptance project on schedule.
4. **Release**: runs the provider matrix and complete solution before publishing images.
   A `v*` tag, or an explicit manual run from `main`, triggers it.
5. **Deploy**: owned by you. Integrios publishes images; how you run them (Compose,
   Kubernetes, etc.) lives in your own infrastructure, not in this repo.

## What the default pipeline does

The main and release tiers publish images to GitHub Container Registry (GHCR) under:

```
ghcr.io/<owner>/<repo>/ingestion
ghcr.io/<owner>/<repo>/admin
ghcr.io/<owner>/<repo>/worker
```

Main images are tagged with the commit SHA, branch name, and `latest`. Release images are
tagged with the commit SHA and, for `v*` tags, semantic-version tags. Each image includes
an SBOM and build provenance attestation.

The three services are separate images because each contains only its own host. Ingestion, Admin,
and Worker compose different capabilities — Worker resolves delivery secrets and Admin never
does — so keeping them in one image would reduce that separation to configuration. All three are
built from a single `docker/Dockerfile`, which selects the host through a `PROJECT` build
argument; the base image pins and the Npgsql GSSAPI workaround therefore exist in exactly one
place.

**The published images are a matched set.** A given version tag assumes all three services run
that same version against a database migrated to the schema they expect. Nothing at runtime
prevents mixing versions, and a release that changes the schema will misbehave if only some
services are rolled forward. Deploy Ingestion, Admin, and Worker together, from the same tag or
digest set, and run migrations before starting them. The deployment reference in
`deploy/compose.yml` enforces this by resolving all three images from a single
`INTEGRIOS_VERSION`; keep that property if you adapt it.

Successful release runs retain a downloadable evidence artifact for 90 days. It contains
the workflow and commit identity, .NET and dependency versions, exact qualification
commands, test logs and TRX results, resolved external image digests, and the published
Ingestion, Admin, and Worker image digests.

The repository pins its .NET SDK, NuGet dependency graph, container versions, and
third-party GitHub Actions. Action references use immutable commit SHAs with a readable
release-version comment; keep that form when adapting the workflow.

Publishing exists only in the main and release tiers. Pull requests, including those from
forks, run only the read-only verification job and never need or receive registry
credentials.

## Publishing to your own registry

The workflow lives in one file you own in your fork. To publish elsewhere, edit the
`package` job in `ci.yml`:

- **Different GHCR namespace**: nothing to change. `images:` uses
  `ghcr.io/${{ github.repository }}/<service>`, so your fork publishes under your own
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
      images: registry.example.com/my-team/integrios/${{ matrix.service.name }}
  ```

- **Multi-arch images** (e.g. ARM hosts): add `platforms: linux/amd64,linux/arm64` to
  the `Build and push` step.

## Consuming the images

Pull the published images by tag or digest from your registry and run them with your own
orchestration. The repository's `compose.yml` is a working reference for how the services,
database, and migrations fit together; adapt it (or translate it to your platform) for
your environment.
