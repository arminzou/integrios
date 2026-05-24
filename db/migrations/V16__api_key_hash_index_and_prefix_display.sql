-- Switch lookup from key_prefix to key_hash.
-- key_prefix becomes a display-only hint (first 12 chars of token); not used for lookup.
DROP INDEX IF EXISTS idx_api_keys_key_prefix;
CREATE UNIQUE INDEX idx_api_keys_key_hash ON api_keys(key_hash);
