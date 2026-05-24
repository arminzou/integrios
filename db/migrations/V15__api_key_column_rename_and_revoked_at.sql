-- Rename api_keys columns to better reflect the single-token credential model.
-- key_id  → key_prefix (the lookup identifier embedded at the start of the token)
-- secret_hash → key_hash (SHA-256 of the full token string)
-- Add revoked_at to capture when a key was revoked for audit purposes.

ALTER TABLE api_keys
    RENAME COLUMN key_id TO key_prefix;

ALTER TABLE api_keys
    RENAME COLUMN secret_hash TO key_hash;

ALTER TABLE api_keys
    ADD COLUMN revoked_at TIMESTAMPTZ;

-- Update the index that was on key_id to reflect the new column name.
DROP INDEX IF EXISTS idx_api_credentials_tenant_id;
CREATE INDEX IF NOT EXISTS idx_api_keys_tenant_id ON api_keys(tenant_id);
CREATE INDEX IF NOT EXISTS idx_api_keys_key_prefix ON api_keys(key_prefix);
