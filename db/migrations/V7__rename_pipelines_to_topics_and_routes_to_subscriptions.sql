ALTER TABLE pipelines RENAME TO topics;
ALTER TABLE routes RENAME TO subscriptions;

ALTER TABLE events RENAME COLUMN pipeline_id TO topic_id;
ALTER TABLE subscriptions RENAME COLUMN pipeline_id TO topic_id;
ALTER TABLE delivery_attempts RENAME COLUMN route_id TO subscription_id;

ALTER INDEX idx_pipelines_tenant_id RENAME TO idx_topics_tenant_id;
ALTER INDEX idx_routes_pipeline_id RENAME TO idx_subscriptions_topic_id;
ALTER INDEX idx_delivery_attempts_route_id RENAME TO idx_delivery_attempts_subscription_id;

ALTER TABLE events RENAME CONSTRAINT fk_events_pipeline TO fk_events_topic;

CREATE TABLE subscription_deliveries (
    id                        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_id                  UUID NOT NULL REFERENCES events(id),
    subscription_id           UUID NOT NULL REFERENCES subscriptions(id),
    destination_connection_id UUID NOT NULL REFERENCES connections(id),
    status                    TEXT NOT NULL DEFAULT 'pending',
    attempt_count             INTEGER NOT NULL DEFAULT 0,
    deliver_after             TIMESTAMPTZ,
    processed_at              TIMESTAMPTZ,
    failed_at                 TIMESTAMPTZ,
    created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_subscription_deliveries_event_subscription UNIQUE (event_id, subscription_id)
);

CREATE INDEX idx_subscription_deliveries_subscription_id ON subscription_deliveries(subscription_id);
CREATE INDEX idx_subscription_deliveries_event_id ON subscription_deliveries(event_id);
CREATE INDEX idx_subscription_deliveries_pending ON subscription_deliveries(status, deliver_after) WHERE processed_at IS NULL;
