# Local Tenant secrets

Each child directory is mounted read-only into one process at `/run/secrets/integrios`, where .NET
key-per-file configuration reads it at startup. Do not commit secret values.

| Directory | Mounted into | File name |
|---|---|---|
| `ingestion/` | Ingestion | `SourceSecrets__<tenant-slug>__<secret-reference>` |
| `worker/` | Worker | `DestinationSecrets__<tenant-slug>__<secret-reference>` |

The file name is the configuration key, with `__` as the section separator; the file content is
the value. For example, the `erp-api-key` reference for the `acme` Tenant:

```bash
printf %s 'secret-value' > secrets/worker/DestinationSecrets__acme__erp-api-key
docker compose restart worker
```

Values load once at startup, so restart the owning service after adding or rotating one. Every
file in a mounted directory becomes a configuration key, except names starting with `ignore.`.
Never place one process's secrets in the other's directory.

Secret files are ignored by Git; the tracked `.gitkeep` files keep Docker from creating the mount
roots as root-owned directories on first use. See
[`docs/setup.md`](../docs/setup.md#tenant-secrets) for the value rules and other ways to supply
the same keys.
