# Local delivery secrets

This directory is mounted read-only into the Worker for local development when the default
file-based secret provider is selected. Do not commit secret values.

Store each value at:

```text
secrets/<tenant-slug>/<reference>
```

For example, create the `erp_api_key` reference for the `acme` Tenant without adding a trailing
newline:

```bash
mkdir -p secrets/acme
printf %s 'secret-value' > secrets/acme/erp_api_key
```

File contents are used exactly as written and are not trimmed. Values must be non-empty, contain
no NUL character, and be no larger than 64 KiB. Header-based authentication also rejects carriage
returns and line feeds. Secret files and Tenant subdirectories are ignored by Git.

See [`docs/setup.md`](../docs/setup.md#delivery-secrets) for provider configuration, validation,
and rotation guidance.
