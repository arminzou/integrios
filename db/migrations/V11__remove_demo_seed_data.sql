-- Remove legacy demo bootstrap data from the shared migration path.
-- Operational setup now happens through the Admin API; demo data belongs in tests.

WITH demo_tenants AS (
    SELECT id
    FROM tenants
    WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
)
DELETE FROM delivery_attempts da
USING demo_tenants dt
WHERE da.event_id IN (
    SELECT e.id
    FROM events e
    WHERE e.tenant_id = dt.id
);

WITH demo_tenants AS (
    SELECT id
    FROM tenants
    WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
)
DELETE FROM subscription_deliveries sd
USING demo_tenants dt
WHERE sd.event_id IN (
    SELECT e.id
    FROM events e
    WHERE e.tenant_id = dt.id
);

WITH demo_tenants AS (
    SELECT id
    FROM tenants
    WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
)
DELETE FROM outbox o
USING demo_tenants dt
WHERE o.event_id IN (
    SELECT e.id
    FROM events e
    WHERE e.tenant_id = dt.id
);

DELETE FROM events
WHERE tenant_id IN (
    SELECT id
    FROM tenants
    WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
);

DELETE FROM subscriptions
WHERE topic_id IN (
    SELECT id
    FROM topics
    WHERE tenant_id IN (
        SELECT id
        FROM tenants
        WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
    )
);

DELETE FROM topic_sources
WHERE topic_id IN (
    SELECT id
    FROM topics
    WHERE tenant_id IN (
        SELECT id
        FROM tenants
        WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
    )
);

DELETE FROM topics
WHERE tenant_id IN (
    SELECT id
    FROM tenants
    WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
);

DELETE FROM api_keys
WHERE tenant_id IN (
    SELECT id
    FROM tenants
    WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
);

DELETE FROM admin_keys
WHERE tenant_id IN (
    SELECT id
    FROM tenants
    WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
);

DELETE FROM connections
WHERE tenant_id IN (
    SELECT id
    FROM tenants
    WHERE slug IN ('demo-swiftpay', 'demo-tradefront')
);

DELETE FROM tenants
WHERE slug IN ('demo-swiftpay', 'demo-tradefront');
