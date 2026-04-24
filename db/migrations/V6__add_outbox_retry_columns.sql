ALTER TABLE outbox
    ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN deliver_after  TIMESTAMPTZ;

-- Index to efficiently find rows that are due for processing or retry.
-- Replaces the existing unprocessed index with one that also checks deliver_after.
DROP INDEX idx_outbox_unprocessed;
CREATE INDEX idx_outbox_pending ON outbox(deliver_after NULLS FIRST, created_at)
    WHERE processed_at IS NULL;
