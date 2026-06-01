# CI/CD

Integrios ships a single GitHub Actions pipeline (`.github/workflows/ci.yml`) you can run
as-is or fork and adapt. It is layered so you adopt only what you need:

1. **Verify** — build and unit-test on every push and pull request. No configuration,
   no secrets. This runs for forks too.
2. **Package** — build container images for the deployable services (`ingress`, `admin`,
   `worker`) and publish them to a container registry.
3. **Deploy** — owned by you. Integrios publishes images; how you run them (Compose,
   Kubernetes, etc.) lives in your own infrastructure, not in this repo.

## What the default pipeline does

`ci.yml` runs Verify on every push and PR. On pushes to the default branch and on `v*`
tags, it also runs Package, publishing images to GitHub Container Registry (GHCR) under:

```
ghcr.io/<owner>/<repo>/ingress
ghcr.io/<owner>/<repo>/admin
ghcr.io/<owner>/<repo>/worker
```

Images are tagged with the commit SHA, the branch name, `latest` on the default branch,
and semver tags on `v*` releases. Each image includes an SBOM and build provenance
attestation.

Publishing is gated to `push` events, so pull requests (including from forks) only run
Verify and never need or expose registry credentials.

## Publishing to your own registry

The pipeline lives in one file you own in your fork. To publish elsewhere, edit the
`package` job in `ci.yml`:

- **Different GHCR namespace** — nothing to change. `images:` uses
  `ghcr.io/${{ github.repository }}/<service>`, so your fork publishes under your own
  owner/repo automatically.
- **A non-GHCR registry** (Docker Hub, a private registry, etc.) — point the `Log in`
  step and the `images:` value at your registry, and supply credentials as repository
  secrets:

  ```yaml
  - name: Log in
    uses: docker/login-action@v3
    with:
      registry: registry.example.com
      username: ${{ secrets.REGISTRY_USERNAME }}
      password: ${{ secrets.REGISTRY_PASSWORD }}

  - name: Derive image metadata
    id: meta
    uses: docker/metadata-action@v5
    with:
      images: registry.example.com/my-team/integrios/${{ matrix.service.name }}
  ```

- **Multi-arch images** (e.g. ARM hosts) — add `platforms: linux/amd64,linux/arm64` to
  the `Build and push` step.

## Consuming the images

Pull the published images by tag or digest from your registry and run them with your own
orchestration. The repository's `compose.yml` is a working reference for how the services,
database, and migrations fit together; adapt it (or translate it to your platform) for
your environment.
