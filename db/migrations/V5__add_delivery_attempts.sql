CREATE TABLE delivery_attempts (
    id                        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_id                  UUID NOT NULL REFERENCES events(id),
    route_id                  UUID NOT NULL REFERENCES routes(id),
    destination_connection_id UUID NOT NULL REFERENCES connections(id),
    attempt_number            INTEGER NOT NULL,
    status                    TEXT NOT NULL,
    request_payload           JSONB,
    response_status_code      INTEGER,
    response_body             TEXT,
    error_message             TEXT,
    started_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at              TIMESTAMPTZ
);

CREATE INDEX idx_delivery_attempts_event_id ON delivery_attempts(event_id);
CREATE INDEX idx_delivery_attempts_route_id ON delivery_attempts(route_id);
