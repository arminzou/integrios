# Local source-verification secrets

This directory is mounted read-only into Ingress for local development when the default file
provider is selected. Store each value at:

```text
secrets/source/<tenant-slug>/<reference>
```

File contents are exact UTF-8, must be non-empty, contain no NUL character, and be no larger than
64 KiB. Secret files and Tenant subdirectories are ignored by Git.

See [`docs/setup.md`](../../docs/setup.md#destination-authentication-secrets) for the shared value
rules and process-isolation contract.
