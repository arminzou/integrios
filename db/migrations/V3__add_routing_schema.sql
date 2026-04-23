-- integrations: platform-level definitions of how to talk to an external system
CREATE TABLE integrations (
    id          UUID PRIMARY KEY,
    key         TEXT NOT NULL UNIQUE,
    name        TEXT NOT NULL,
    direction   TEXT NOT NULL,
    auth_scheme TEXT NOT NULL DEFAULT 'none',
    status      TEXT NOT NULL DEFAULT 'active',
    description TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- connections: tenant-scoped configured instances of an integration
CREATE TABLE connections (
    id              UUID PRIMARY KEY,
    tenant_id       UUID NOT NULL REFERENCES tenants(id),
    integration_id  UUID NOT NULL REFERENCES integrations(id),
    name            TEXT NOT NULL,
    config          JSONB NOT NULL DEFAULT '{}',
    secret_refs     JSONB NOT NULL DEFAULT '{}',
    status          TEXT NOT NULL DEFAULT 'active',
    environment     TEXT,
    description     TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_connections_tenant_id ON connections(tenant_id);

-- pipelines: tenant-owned event processing pipelines scoped to a source connection
CREATE TABLE pipelines (
    id                   UUID PRIMARY KEY,
    tenant_id            UUID NOT NULL REFERENCES tenants(id),
    name                 TEXT NOT NULL,
    source_connection_id UUID NOT NULL REFERENCES connections(id),
    event_types          TEXT[] NOT NULL DEFAULT '{}',
    status               TEXT NOT NULL DEFAULT 'active',
    description          TEXT,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_pipelines_tenant_id ON pipelines(tenant_id);

-- routes: branches within a pipeline that match events and deliver to a destination connection
CREATE TABLE routes (
    id                        UUID PRIMARY KEY,
    pipeline_id               UUID NOT NULL REFERENCES pipelines(id),
    name                      TEXT NOT NULL,
    match_rules               JSONB NOT NULL DEFAULT '{}',
    destination_connection_id UUID NOT NULL REFERENCES connections(id),
    transform_config          JSONB,
    delivery_policy           JSONB,
    status                    TEXT NOT NULL DEFAULT 'active',
    order_index               INTEGER NOT NULL DEFAULT 0,
    description               TEXT,
    created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_routes_pipeline_id ON routes(pipeline_id);

-- enforce referential integrity on events now that pipelines and connections exist
ALTER TABLE events
    ADD CONSTRAINT fk_events_pipeline   FOREIGN KEY (pipeline_id)          REFERENCES pipelines(id),
    ADD CONSTRAINT fk_events_connection FOREIGN KEY (source_connection_id) REFERENCES connections(id);
