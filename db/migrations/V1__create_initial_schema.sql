-- tenants
CREATE TABLE tenants (
    id          UUID PRIMARY KEY,
    slug        TEXT NOT NULL UNIQUE,
    name        TEXT NOT NULL,
    status      TEXT NOT NULL DEFAULT 'active',
    environment TEXT,
    description TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- api_credentials
CREATE TABLE api_credentials (
    id           UUID PRIMARY KEY,
    tenant_id    UUID NOT NULL REFERENCES tenants(id),
    name         TEXT NOT NULL,
    key_id       TEXT NOT NULL UNIQUE,
    secret_hash  TEXT NOT NULL,
    scopes       TEXT[] NOT NULL DEFAULT '{}',
    status       TEXT NOT NULL DEFAULT 'active',
    description  TEXT,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at   TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ
);

CREATE INDEX idx_api_credentials_tenant_id ON api_credentials(tenant_id);

-- events
CREATE TABLE events (
    id                  UUID PRIMARY KEY,
    tenant_id           UUID NOT NULL REFERENCES tenants(id),
    integration_flow_id UUID,
    source_connector_id UUID,
    source_event_id     TEXT,
    event_type          TEXT NOT NULL,
    payload             JSONB NOT NULL,
    metadata            JSONB,
    idempotency_key     TEXT,
    status              TEXT NOT NULL DEFAULT 'accepted',
    accepted_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at        TIMESTAMPTZ,
    failed_at           TIMESTAMPTZ
);

CREATE INDEX idx_events_tenant_id ON events(tenant_id);
CREATE UNIQUE INDEX idx_events_idempotency ON events(tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL;

-- outbox
CREATE TABLE outbox (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_id     UUID NOT NULL REFERENCES events(id),
    payload      JSONB NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at TIMESTAMPTZ
);

CREATE INDEX idx_outbox_unprocessed ON outbox(created_at) WHERE processed_at IS NULL;
