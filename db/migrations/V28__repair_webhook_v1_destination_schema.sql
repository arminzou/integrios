DO $$
DECLARE
    webhook_id UUID := '00000000-0000-0000-0000-000000000001';
    legacy_schema JSONB := '{"type":"object","properties":{},"additionalProperties":true}'::jsonb;
    stored_schema JSONB;
BEGIN
    IF EXISTS (
        SELECT 1 FROM integrations
        WHERE id = webhook_id
          AND (key IS DISTINCT FROM 'webhook' OR contract_version IS DISTINCT FROM 1)) THEN
        RAISE EXCEPTION 'V28 found the well-known webhook Integration id assigned to an unexpected contract';
    END IF;

    IF EXISTS (
        SELECT 1 FROM integrations
        WHERE key = 'webhook' AND contract_version = 1 AND id IS DISTINCT FROM webhook_id) THEN
        RAISE EXCEPTION 'V28 found webhook contract version 1 assigned to an unexpected id';
    END IF;

    SELECT manifest->'destination_configuration_schema'
    INTO stored_schema
    FROM integrations
    WHERE id = webhook_id AND key = 'webhook' AND contract_version = 1;

    IF FOUND AND stored_schema IS DISTINCT FROM legacy_schema THEN
        RAISE EXCEPTION 'V28 cannot repair webhook contract version 1 because its destination schema has drifted';
    END IF;
END
$$;

ALTER TABLE integrations DISABLE TRIGGER integrations_reject_functional_update;

UPDATE integrations
SET manifest = jsonb_set(
    manifest,
    '{destination_configuration_schema}',
    '{"type":"object","properties":{"url":{"type":"string","format":"uri"}},"required":["url"],"additionalProperties":true}'::jsonb)
WHERE id = '00000000-0000-0000-0000-000000000001'
  AND key = 'webhook'
  AND contract_version = 1;

ALTER TABLE integrations ENABLE TRIGGER integrations_reject_functional_update;
