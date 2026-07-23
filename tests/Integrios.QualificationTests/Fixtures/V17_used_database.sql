INSERT INTO tenants (id, slug, name, status)
VALUES (
    '17000000-0000-0000-0000-000000000001',
    'v17-used-tenant',
    'V17 Used Tenant',
    'active'
);

INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
VALUES (
    '17000000-0000-0000-0000-000000000002',
    '17000000-0000-0000-0000-000000000001',
    '00000000-0000-0000-0000-000000000001',
    'retained-webhook',
    '{"url":"https://example.invalid/v17"}',
    'active'
);

UPDATE integrations
SET name = 'Drifted Webhook',
    direction = 'destination',
    status = 'disabled',
    description = 'Retained used row awaiting Bootstrap reconciliation'
WHERE id = '00000000-0000-0000-0000-000000000001';
