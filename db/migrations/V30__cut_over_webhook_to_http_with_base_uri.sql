DO $$
DECLARE
    http_id UUID := '00000000-0000-0000-0000-000000000001';
    legacy_destination_schema JSONB := '{"type":"object","properties":{"url":{"type":"string","format":"uri"}},"required":["url"],"additionalProperties":true}'::jsonb;
    legacy_source_verification JSONB := '{"allow_unverified":true,"schemes":[]}'::jsonb;
    legacy_destination_authentication JSONB := '{"allow_unauthenticated":true,"schemes":[]}'::jsonb;
    stored_manifest JSONB;
    offending_connection UUID;
BEGIN
    IF EXISTS (
        SELECT 1 FROM integrations
        WHERE id = http_id
          AND (key IS DISTINCT FROM 'webhook' OR contract_version IS DISTINCT FROM 1)) THEN
        RAISE EXCEPTION 'V30 found the well-known http Integration id assigned to an unexpected contract';
    END IF;

    IF EXISTS (
        SELECT 1 FROM integrations
        WHERE key = 'webhook' AND contract_version = 1 AND id IS DISTINCT FROM http_id) THEN
        RAISE EXCEPTION 'V30 found webhook contract version 1 assigned to an unexpected id';
    END IF;

    SELECT manifest INTO stored_manifest
    FROM integrations
    WHERE id = http_id AND key = 'webhook' AND contract_version = 1;

    IF FOUND AND (
        stored_manifest->'destination_configuration_schema' IS DISTINCT FROM legacy_destination_schema
        OR stored_manifest->'source_verification' IS DISTINCT FROM legacy_source_verification
        OR stored_manifest->'destination_authentication' IS DISTINCT FROM legacy_destination_authentication) THEN
        RAISE EXCEPTION 'V30 cannot cut over webhook contract version 1 because its manifest has drifted';
    END IF;

    SELECT id INTO offending_connection
    FROM connections
    WHERE integration_id = http_id
      AND config ? 'url'
      AND config->>'url' ~ '[?#]'
    LIMIT 1;

    IF offending_connection IS NOT NULL THEN
        RAISE EXCEPTION 'V30 cannot migrate Connection % because its destination url carries a query string or fragment; repair it to an equivalent base_uri manually', offending_connection;
    END IF;

    SELECT id INTO offending_connection
    FROM connections
    WHERE integration_id = http_id
      AND config ? 'url'
      AND config - 'url' <> '{}'::jsonb
    LIMIT 1;

    IF offending_connection IS NOT NULL THEN
        RAISE EXCEPTION 'V30 cannot migrate Connection % because its destination config contains fields other than url; repair it to the closed base_uri contract manually', offending_connection;
    END IF;
END
$$;

ALTER TABLE integrations DISABLE TRIGGER integrations_reject_functional_update;

UPDATE integrations
SET key = 'http',
    supported_auth_schemes = '["api_key_header","bearer_token"]'::jsonb,
    manifest = jsonb_set(
        jsonb_set(
            jsonb_set(
                manifest,
                '{key}',
                '"http"'::jsonb),
            '{destination_configuration_schema}',
            '{"type":"object","properties":{"base_uri":{"type":"string","format":"uri"}},"required":["base_uri"],"additionalProperties":false}'::jsonb),
        '{destination_authentication}',
        '{"allow_unauthenticated":true,"schemes":[{"scheme":"api_key_header","required_config":["header_name"],"required_secret_refs":["api_key"]},{"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}]}'::jsonb)
WHERE id = '00000000-0000-0000-0000-000000000001'
  AND key = 'webhook'
  AND contract_version = 1;

ALTER TABLE integrations ENABLE TRIGGER integrations_reject_functional_update;

UPDATE connections
SET config = (config - 'url') || jsonb_build_object('base_uri', config->'url')
WHERE integration_id = '00000000-0000-0000-0000-000000000001'
  AND config ? 'url';
