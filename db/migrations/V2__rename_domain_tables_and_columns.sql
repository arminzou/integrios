ALTER TABLE api_credentials RENAME TO api_keys;

ALTER INDEX idx_api_credentials_tenant_id RENAME TO idx_api_keys_tenant_id;

ALTER TABLE events RENAME COLUMN integration_flow_id TO pipeline_id;
ALTER TABLE events RENAME COLUMN source_connector_id TO source_connection_id;
