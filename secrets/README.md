# Local Tenant secrets

These directories hold Tenant secret values only. Each is mounted read-only into one process at
`/run/secrets/integrios`, where .NET key-per-file configuration reads it at startup. Supply
deployment credentials such as database connection strings through environment variables. Do not
commit secret values.

| Directory | Mounted into | File name |
|---|---|---|
| `sources/` | Ingestion only | `SourceSecrets__<tenant-slug>__<secret-reference>` |
| `destinations/` | Worker only | `DestinationSecrets__<tenant-slug>__<secret-reference>` |

The file name is the configuration key, with `__` as the section separator; the file content is
the value. For example, the `erp-api-key` reference for the `acme` Tenant:

```bash
printf %s 'secret-value' > secrets/destinations/DestinationSecrets__acme__erp-api-key
docker compose restart worker
```

Each directory also has an empty `ignore.` example file. .NET skips these files. For a new
Tenant, copy the relevant example, remove the `ignore.` prefix, replace `example-tenant` and the
sample reference with the values authored for that Tenant, then write the actual secret value into
the new file. Keep the original example empty. Source files belong only in `sources/`; Destination
files belong only in `destinations/`.

Values load once at startup, so restart the owning service after adding or rotating one. Every
file in a mounted directory becomes a configuration key, except names starting with `ignore.`.
Never place one process's secrets in the other's directory.

Secret files are ignored by Git; the tracked example files keep Docker from creating the mount
roots as root-owned directories on first use. See
[`docs/setup.md`](../docs/setup.md#tenant-secrets) for the value rules and other ways to supply
the same keys.
