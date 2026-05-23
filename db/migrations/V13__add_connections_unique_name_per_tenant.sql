ALTER TABLE connections
    ADD CONSTRAINT uq_connections_tenant_name UNIQUE (tenant_id, name);
