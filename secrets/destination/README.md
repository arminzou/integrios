# Local destination-authentication secrets

This directory is mounted read-only into Worker for local development when the default file
provider is selected. Store each value at:

```text
secrets/destination/<tenant-slug>/<reference>
```

For example, create the `erp_api_key` reference for the `acme` Tenant without adding a trailing
newline:

```bash
mkdir -p secrets/destination/acme
printf %s 'secret-value' > secrets/destination/acme/erp_api_key
```

File contents are used exactly as written and are not trimmed. Values must be non-empty, contain
no NUL character, and be no larger than 64 KiB. Header-based authentication also rejects carriage
returns and line feeds. Secret files and Tenant subdirectories are ignored by Git.

See [`docs/setup.md`](../../docs/setup.md#destination-authentication-secrets) for provider
configuration, validation, and rotation guidance.
