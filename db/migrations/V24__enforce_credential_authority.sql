-- Legacy Tenant-scoped AdminKeys were never a supported authority boundary. Revoke them before
-- removing tenant scope so the migration cannot silently promote one to deployment-wide access.
UPDATE admin_keys
SET revoked_at = COALESCE(revoked_at, now())
WHERE tenant_id IS NOT NULL;

ALTER TABLE admin_keys
    DROP COLUMN tenant_id;

-- An ApiKey resolves exactly one Tenant and grants the complete generic Event intake/read/replay
-- capability. Provider credentials remain Connection secrets, so per-action scopes add no value.
ALTER TABLE api_keys
    DROP COLUMN scopes;
