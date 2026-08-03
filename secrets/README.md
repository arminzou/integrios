# Local directional secrets

This parent directory separates the source-verification values available to Ingress from the
destination-authentication values available to Worker. Each process receives only its own child
directory as a read-only mount. Do not commit secret values.

- [`source/`](./source/README.md) contains source-verification values for Ingress.
- [`destination/`](./destination/README.md) contains destination-authentication values for Worker.

Tenant directories and secret files below either child are ignored by Git. The tracked scaffolding
keeps Docker from creating the mount roots as root-owned directories on first use.
