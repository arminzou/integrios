-- topic_sources: replaces topics.source_connection_id with a join table
-- allowing multiple source connections per topic.
CREATE TABLE topic_sources (
    topic_id      UUID NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
    connection_id UUID NOT NULL REFERENCES connections(id),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (topic_id, connection_id)
);

CREATE INDEX idx_topic_sources_connection_id ON topic_sources(connection_id);

-- Migrate existing single source into the join table
INSERT INTO topic_sources (topic_id, connection_id)
SELECT id, source_connection_id
FROM topics
WHERE source_connection_id IS NOT NULL;

ALTER TABLE topics DROP COLUMN source_connection_id;
ALTER TABLE topics DROP COLUMN event_types;
