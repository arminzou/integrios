ALTER TABLE connections
    ADD COLUMN source_verification JSONB,
    ADD COLUMN destination_authentication JSONB;

DO $$
DECLARE
    repair_ids TEXT;
BEGIN
    SELECT string_agg(c.id::text, ', ' ORDER BY c.id)
    INTO repair_ids
    FROM connections c
    WHERE c.auth IS NOT NULL
      AND EXISTS (
          SELECT 1 FROM topic_sources ts
          JOIN topics t ON t.tenant_id = ts.tenant_id AND t.id = ts.topic_id
          WHERE ts.connection_id = c.id AND t.status = 'active')
      AND EXISTS (SELECT 1 FROM subscriptions s WHERE s.destination_connection_id = c.id AND s.status = 'active');

    IF repair_ids IS NOT NULL THEN
        RAISE EXCEPTION USING
            MESSAGE = 'V27 cannot infer one meaning for legacy Connection auth used as both a source and a destination: ' || repair_ids,
            HINT = 'Remove one active use or replace the Connection with separate source and destination Connections, then rerun the migration.';
    END IF;

    SELECT string_agg(c.id::text, ', ' ORDER BY c.id)
    INTO repair_ids
    FROM connections c
    WHERE c.auth IS NOT NULL
      AND EXISTS (
          SELECT 1 FROM topic_sources ts
          JOIN topics t ON t.tenant_id = ts.tenant_id AND t.id = ts.topic_id
          WHERE ts.connection_id = c.id AND t.status = 'active')
      AND NOT EXISTS (SELECT 1 FROM subscriptions s WHERE s.destination_connection_id = c.id AND s.status = 'active');

    IF repair_ids IS NOT NULL THEN
        RAISE EXCEPTION USING
            MESSAGE = 'V27 cannot reinterpret legacy destination authentication as source verification: ' || repair_ids,
            HINT = 'Remove the legacy auth block or recreate the source Connection with an explicit source_verification selection, then rerun the migration.';
    END IF;

    SELECT string_agg(c.id::text, ', ' ORDER BY c.id)
    INTO repair_ids
    FROM connections c
    WHERE c.auth IS NOT NULL
      AND NOT EXISTS (
          SELECT 1 FROM topic_sources ts
          JOIN topics t ON t.tenant_id = ts.tenant_id AND t.id = ts.topic_id
          WHERE ts.connection_id = c.id AND t.status = 'active')
      AND NOT EXISTS (SELECT 1 FROM subscriptions s WHERE s.destination_connection_id = c.id AND s.status = 'active');

    IF repair_ids IS NOT NULL THEN
        RAISE EXCEPTION USING
            MESSAGE = 'V27 cannot infer a use for unused legacy Connection auth: ' || repair_ids,
            HINT = 'Attach the Connection to its intended destination Subscription or remove the legacy auth block, then rerun the migration.';
    END IF;

    SELECT string_agg(c.id::text, ', ' ORDER BY c.id)
    INTO repair_ids
    FROM connections c
    JOIN integrations i ON i.id = c.integration_id
    WHERE c.auth IS NOT NULL
      AND EXISTS (SELECT 1 FROM subscriptions s WHERE s.destination_connection_id = c.id AND s.status = 'active')
      AND (
          jsonb_typeof(c.auth) IS DISTINCT FROM 'object'
          OR jsonb_typeof(c.auth->'scheme') IS DISTINCT FROM 'string'
          OR jsonb_typeof(c.auth->'config') IS DISTINCT FROM 'object'
          OR jsonb_typeof(c.auth->'secret_refs') IS DISTINCT FROM 'object'
          OR NOT EXISTS (
              SELECT 1
              FROM jsonb_array_elements(i.manifest->'destination_authentication_schemes') scheme
              WHERE scheme->>'scheme' = c.auth->>'scheme'
                AND NOT EXISTS (
                    SELECT 1
                    FROM jsonb_array_elements_text(scheme->'required_config') required(field)
                    WHERE NOT ((c.auth->'config') ? required.field)
                       OR (c.auth->'config')->required.field = 'null'::jsonb)
                AND NOT EXISTS (
                    SELECT 1
                    FROM jsonb_array_elements_text(scheme->'required_secret_refs') required(field)
                    WHERE NOT ((c.auth->'secret_refs') ? required.field)
                       OR (c.auth->'secret_refs')->required.field = 'null'::jsonb)
          )
      );

    IF repair_ids IS NOT NULL THEN
        RAISE EXCEPTION USING
            MESSAGE = 'V27 cannot migrate malformed or unsupported legacy destination authentication: ' || repair_ids,
            HINT = 'Repair each auth envelope so its scheme and required fields match the referenced Integration version, then rerun the migration.';
    END IF;
END
$$;

UPDATE connections c
SET destination_authentication = c.auth
WHERE c.auth IS NOT NULL
  AND EXISTS (SELECT 1 FROM subscriptions s WHERE s.destination_connection_id = c.id AND s.status = 'active');

ALTER TABLE connections
    DROP COLUMN auth,
    ADD CONSTRAINT ck_connections_source_verification_object
        CHECK (source_verification IS NULL OR jsonb_typeof(source_verification) = 'object'),
    ADD CONSTRAINT ck_connections_destination_authentication_object
        CHECK (destination_authentication IS NULL OR jsonb_typeof(destination_authentication) = 'object');
