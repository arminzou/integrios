-- ADR-0046 split the vocabulary: topic_sources stay active/inactive (reversible),
-- source_endpoints become active/revoked (permanent — reactivating the association mints a
-- new UUID/callback path, so the old endpoint row is never revived).

-- Drop the existing CHECK constraints so we can rename the column and update the values.
-- The named constraint covers the (status, revoked_at) pairing; the unnamed inline CHECK
-- on the status column covers the value set.
ALTER TABLE source_endpoints
    DROP CONSTRAINT IF EXISTS ck_source_endpoints_inactive_at,
    DROP CONSTRAINT IF EXISTS source_endpoints_status_check;

ALTER TABLE source_endpoints
    RENAME COLUMN inactive_at TO revoked_at;

-- Backfill existing 'inactive' rows before the new CHECK constraints below are added, since
-- ADD CONSTRAINT validates every existing row immediately and 'inactive' is no longer permitted.
UPDATE source_endpoints
SET status = 'revoked'
WHERE status = 'inactive';

ALTER TABLE source_endpoints
    ADD CONSTRAINT ck_source_endpoints_status CHECK (status IN ('active', 'revoked')),
    ADD CONSTRAINT ck_source_endpoints_revoked_at CHECK (
        (status = 'active' AND revoked_at IS NULL)
        OR (status = 'revoked' AND revoked_at IS NOT NULL));
