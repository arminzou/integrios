ALTER TABLE topics
    ADD CONSTRAINT uq_topics_tenant_name UNIQUE (tenant_id, name);
