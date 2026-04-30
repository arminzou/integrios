-- Remove the legacy fixed demo bootstrap data from the shared migration path.
--
-- Safety rule: only delete the exact rows inserted by V4's deterministic demo seed.
-- Do not delete operator-created tenants just because they reused a demo slug.

DELETE FROM delivery_attempts
WHERE event_id IN (
    SELECT id
    FROM events
    WHERE tenant_id IN (
        'aaaaaaaa-0000-0000-0000-000000000001',
        'bbbbbbbb-0000-0000-0000-000000000001'
    )
);

DELETE FROM subscription_deliveries
WHERE event_id IN (
    SELECT id
    FROM events
    WHERE tenant_id IN (
        'aaaaaaaa-0000-0000-0000-000000000001',
        'bbbbbbbb-0000-0000-0000-000000000001'
    )
);

DELETE FROM outbox
WHERE event_id IN (
    SELECT id
    FROM events
    WHERE tenant_id IN (
        'aaaaaaaa-0000-0000-0000-000000000001',
        'bbbbbbbb-0000-0000-0000-000000000001'
    )
);

DELETE FROM events
WHERE tenant_id IN (
    'aaaaaaaa-0000-0000-0000-000000000001',
    'bbbbbbbb-0000-0000-0000-000000000001'
);

DELETE FROM subscriptions
WHERE id IN (
    'aaaaaaaa-0000-0000-0000-000000000007',
    'aaaaaaaa-0000-0000-0000-000000000008',
    'bbbbbbbb-0000-0000-0000-000000000007',
    'bbbbbbbb-0000-0000-0000-000000000008'
);

DELETE FROM topic_sources
WHERE topic_id IN (
    'aaaaaaaa-0000-0000-0000-000000000006',
    'bbbbbbbb-0000-0000-0000-000000000006'
);

DELETE FROM topics
WHERE id IN (
    'aaaaaaaa-0000-0000-0000-000000000006',
    'bbbbbbbb-0000-0000-0000-000000000006'
);

DELETE FROM api_keys
WHERE id IN (
    'aaaaaaaa-0000-0000-0000-000000000002',
    'bbbbbbbb-0000-0000-0000-000000000002'
);

DELETE FROM admin_keys
WHERE tenant_id IN (
    'aaaaaaaa-0000-0000-0000-000000000001',
    'bbbbbbbb-0000-0000-0000-000000000001'
);

DELETE FROM connections
WHERE id IN (
    'aaaaaaaa-0000-0000-0000-000000000003',
    'aaaaaaaa-0000-0000-0000-000000000004',
    'aaaaaaaa-0000-0000-0000-000000000005',
    'bbbbbbbb-0000-0000-0000-000000000003',
    'bbbbbbbb-0000-0000-0000-000000000004',
    'bbbbbbbb-0000-0000-0000-000000000005'
);

DELETE FROM tenants
WHERE id IN (
    'aaaaaaaa-0000-0000-0000-000000000001',
    'bbbbbbbb-0000-0000-0000-000000000001'
)
AND slug IN ('demo-swiftpay', 'demo-tradefront')
AND environment = 'demo';
