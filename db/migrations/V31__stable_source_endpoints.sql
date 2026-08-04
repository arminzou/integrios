-- Topic/source associations become lifecycle-bearing rows so accepted Events can retain their
-- existing association foreign key after an Operator removes the source from current authoring.
ALTER TABLE topic_sources
    ADD COLUMN status TEXT NOT NULL DEFAULT 'active',
    ADD COLUMN retired_at TIMESTAMPTZ,
    ADD CONSTRAINT ck_topic_sources_status CHECK (status IN ('active', 'retired')),
    ADD CONSTRAINT ck_topic_sources_retired_at CHECK (
        (status = 'active' AND retired_at IS NULL)
        OR (status = 'retired' AND retired_at IS NOT NULL));

-- The association row must remain for historical Event foreign keys, but new direct writes must
-- retain the pre-V31 guarantee that an Event can reference only a currently configured source.
CREATE FUNCTION events_require_active_topic_source()
RETURNS TRIGGER AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM topic_sources ts
        WHERE ts.tenant_id = NEW.tenant_id
          AND ts.topic_id = NEW.topic_id
          AND ts.connection_id = NEW.source_connection_id
          AND ts.status = 'active'
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23503',
            CONSTRAINT = 'fk_events_topic_source_active',
            MESSAGE = 'An Event source Connection must be actively associated with its Topic.';
    END IF;

    RETURN NEW;
END
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_events_require_active_topic_source
BEFORE INSERT OR UPDATE OF tenant_id, topic_id, source_connection_id ON events
FOR EACH ROW
EXECUTE FUNCTION events_require_active_topic_source();

CREATE TABLE source_endpoints (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      UUID NOT NULL,
    topic_id       UUID NOT NULL,
    connection_id  UUID NOT NULL,
    callback_path  TEXT NOT NULL UNIQUE,
    status         TEXT NOT NULL DEFAULT 'active'
                       CHECK (status IN ('active', 'retired')),
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    retired_at     TIMESTAMPTZ,
    CONSTRAINT fk_source_endpoints_topic_source
        FOREIGN KEY (tenant_id, topic_id, connection_id)
        REFERENCES topic_sources (tenant_id, topic_id, connection_id),
    CONSTRAINT ck_source_endpoints_retired_at CHECK (
        (status = 'active' AND retired_at IS NULL)
        OR (status = 'retired' AND retired_at IS NOT NULL))
);

CREATE UNIQUE INDEX uq_source_endpoints_active_association
    ON source_endpoints (tenant_id, topic_id, connection_id)
    WHERE status = 'active';

CREATE INDEX idx_source_endpoints_association
    ON source_endpoints (tenant_id, topic_id, connection_id, created_at);

-- Only Integrations that select a registered source-adapter contract receive endpoints. Existing
-- generic Event-producer associations remain endpoint-free.
WITH endpoint_candidates AS (
    SELECT
        gen_random_uuid() AS endpoint_id,
        ts.tenant_id,
        ts.topic_id,
        ts.connection_id,
        i.key AS integration_key
    FROM topic_sources ts
    JOIN connections c
      ON c.tenant_id = ts.tenant_id
     AND c.id = ts.connection_id
    JOIN integrations i ON i.id = c.integration_id
    WHERE ts.status = 'active'
      AND jsonb_typeof(i.manifest->'source_adapter') = 'object'
)
INSERT INTO source_endpoints (
    id, tenant_id, topic_id, connection_id, callback_path, status)
SELECT
    endpoint_id,
    tenant_id,
    topic_id,
    connection_id,
    '/webhooks/' || integration_key || '/' || endpoint_id::text,
    'active'
FROM endpoint_candidates;
