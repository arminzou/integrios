-- Make Tenant ownership part of the Topic/source association so cross-Tenant
-- relationships cannot be created by direct database writes.
ALTER TABLE topic_sources
    ADD COLUMN tenant_id UUID;

UPDATE topic_sources ts
SET tenant_id = t.tenant_id
FROM topics t
WHERE t.id = ts.topic_id;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM topic_sources ts
        JOIN connections c ON c.id = ts.connection_id
        WHERE c.tenant_id <> ts.tenant_id
    ) THEN
        RAISE EXCEPTION USING
            MESSAGE = 'V23 cannot enforce Tenant-scoped Topic sources because a Topic is associated with a Connection from another Tenant.',
            HINT = 'Remove or replace cross-Tenant topic_sources rows, then rerun the migration.';
    END IF;
END
$$;

ALTER TABLE topic_sources
    ALTER COLUMN tenant_id SET NOT NULL,
    DROP CONSTRAINT topic_sources_pkey,
    DROP CONSTRAINT topic_sources_topic_id_fkey,
    DROP CONSTRAINT topic_sources_connection_id_fkey;

ALTER TABLE topics
    ADD CONSTRAINT uq_topics_tenant_id_id UNIQUE (tenant_id, id);

ALTER TABLE connections
    ADD CONSTRAINT uq_connections_tenant_id_id UNIQUE (tenant_id, id);

ALTER TABLE topic_sources
    ADD CONSTRAINT topic_sources_pkey PRIMARY KEY (tenant_id, topic_id, connection_id),
    ADD CONSTRAINT fk_topic_sources_topic_tenant
        FOREIGN KEY (tenant_id, topic_id) REFERENCES topics (tenant_id, id) ON DELETE CASCADE,
    ADD CONSTRAINT fk_topic_sources_connection_tenant
        FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id);

CREATE INDEX idx_topic_sources_topic_id ON topic_sources(topic_id);

-- Existing nullable source provenance is retained intentionally. NOT VALID
-- leaves historical nulls readable while PostgreSQL enforces the check for
-- every row written after this migration.
ALTER TABLE events
    ADD CONSTRAINT ck_events_source_connection_required
        CHECK (source_connection_id IS NOT NULL) NOT VALID;

-- These constraints are validated against existing rows rather than added
-- NOT VALID. No released intake path has ever written events.source_connection_id,
-- so every existing row is null there and MATCH SIMPLE skips it. Hand-written
-- rows that violate Tenant ownership or association fail migration instead of
-- carrying invalid provenance forward.
ALTER TABLE events
    DROP CONSTRAINT fk_events_topic,
    DROP CONSTRAINT fk_events_connection,
    ADD CONSTRAINT fk_events_topic_tenant
        FOREIGN KEY (tenant_id, topic_id) REFERENCES topics (tenant_id, id),
    ADD CONSTRAINT fk_events_source_connection_tenant
        FOREIGN KEY (tenant_id, source_connection_id) REFERENCES connections (tenant_id, id),
    ADD CONSTRAINT fk_events_topic_source
        FOREIGN KEY (tenant_id, topic_id, source_connection_id)
        REFERENCES topic_sources (tenant_id, topic_id, connection_id);
