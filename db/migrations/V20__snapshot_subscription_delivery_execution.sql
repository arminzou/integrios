-- Execution snapshots (destination_url, integration_key, destination_auth) are captured at fanout
-- from the destination connection as it existed then. Pre-existing delivery rows predate the
-- snapshot and cannot be backfilled reliably: the source connection may have been edited or
-- repointed since fanout, so any value we synthesized would misrepresent what those deliveries were
-- meant to execute. Fail loud rather than guess. Operators must drain subscription_deliveries
-- before applying this migration.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM subscription_deliveries) THEN
        RAISE EXCEPTION 'V20 requires subscription_deliveries to be empty before execution snapshots are introduced';
    END IF;
END $$;

-- destination_url is nullable: connection config is free-form and a destination may lack a url.
-- A missing url must degrade to a graceful delivery failure at dispatch, not a NOT NULL violation
-- that stalls fanout. integration_key stays NOT NULL: integrations.key is NOT NULL and fanout
-- inner-joins integrations over a NOT NULL foreign key, so it is always present.
ALTER TABLE subscription_deliveries
    ADD COLUMN destination_url TEXT,
    ADD COLUMN integration_key TEXT NOT NULL,
    ADD COLUMN destination_auth JSONB;

ALTER TABLE subscriptions
    DROP COLUMN delivery_policy,
    DROP COLUMN dlq_enabled;
