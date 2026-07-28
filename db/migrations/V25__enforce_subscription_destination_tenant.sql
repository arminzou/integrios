-- Refuse to preserve an invalid cross-Tenant Subscription relationship. The
-- Operator must repair existing configuration before Tenant ownership can be
-- made an enforced part of the relationship.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM subscriptions s
        JOIN topics t ON t.id = s.topic_id
        JOIN connections c ON c.id = s.destination_connection_id
        WHERE t.tenant_id <> c.tenant_id
    ) THEN
        RAISE EXCEPTION USING
            MESSAGE = 'V25 cannot enforce Tenant-scoped Subscription destinations because a Subscription references a Connection from another Tenant.',
            HINT = 'Replace the cross-Tenant destination Connection on each Subscription, then rerun the migration.';
    END IF;
END
$$;

ALTER TABLE subscriptions
    ADD COLUMN tenant_id UUID;

UPDATE subscriptions s
SET tenant_id = t.tenant_id
FROM topics t
WHERE t.id = s.topic_id;

ALTER TABLE subscriptions
    ALTER COLUMN tenant_id SET NOT NULL,
    DROP CONSTRAINT routes_pipeline_id_fkey,
    DROP CONSTRAINT routes_destination_connection_id_fkey,
    ADD CONSTRAINT fk_subscriptions_topic_tenant
        FOREIGN KEY (tenant_id, topic_id) REFERENCES topics (tenant_id, id),
    ADD CONSTRAINT fk_subscriptions_destination_connection_tenant
        FOREIGN KEY (tenant_id, destination_connection_id) REFERENCES connections (tenant_id, id);
