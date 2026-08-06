ALTER TABLE subscriptions
    ADD COLUMN http_delivery JSONB NOT NULL DEFAULT
        '{"version":1,"method":"POST","headers":{},"body":"json"}'::jsonb;

ALTER TABLE subscription_deliveries
    ADD COLUMN http_execution_snapshot JSONB;

-- The pre-V33 destination_url and destination_auth already correlated the effective request
-- target and authentication selection on each delivery, and POST-with-a-JSON-body was the only
-- request shape that existed. Together they are a faithful execution snapshot for every existing
-- row. A legacy null url deliberately becomes an empty string so dispatch retains the prior
-- graceful request-construction failure instead of blocking migration/fanout.
UPDATE subscription_deliveries
SET http_execution_snapshot = jsonb_build_object(
        'version', 1,
        'base_uri', COALESCE(destination_url, ''),
        'request', '{"version":1,"method":"POST","headers":{},"body":"json"}'::jsonb)
    || CASE
        WHEN destination_auth IS NULL THEN '{}'::jsonb
        ELSE jsonb_build_object('destination_authentication', destination_auth)
    END;

ALTER TABLE subscription_deliveries
    ALTER COLUMN http_execution_snapshot SET NOT NULL,
    DROP COLUMN destination_url,
    DROP COLUMN destination_auth;
