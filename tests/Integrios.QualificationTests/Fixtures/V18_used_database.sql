INSERT INTO tenants (id, slug, name, status)
VALUES (
    '18000000-0000-0000-0000-000000000001',
    'v18-used-tenant',
    'V18 Used Tenant',
    'active'
);

INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
VALUES (
    '18000000-0000-0000-0000-000000000002',
    '18000000-0000-0000-0000-000000000001',
    '00000000-0000-0000-0000-000000000001',
    'retained-webhook',
    '{"url":"https://example.invalid/v18"}',
    'active'
);

UPDATE admin_keys
SET secret_hash = 'sha256:1111111111111111111111111111111111111111111111111111111111111111',
    name = 'Operator Replaced Global Admin Key'
WHERE tenant_id IS NULL
  AND public_key = 'global_admin_key';
