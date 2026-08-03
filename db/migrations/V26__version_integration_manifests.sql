ALTER TABLE integrations
    ADD COLUMN contract_version INTEGER NOT NULL DEFAULT 1,
    ADD COLUMN manifest_schema_version INTEGER NOT NULL DEFAULT 1,
    ADD COLUMN manifest JSONB;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM integrations WHERE direction NOT IN ('source', 'destination', 'both')) THEN
        RAISE EXCEPTION 'V26 cannot migrate Integration rows with an unsupported direction; repair them before retrying';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM integrations
        CROSS JOIN LATERAL jsonb_array_elements_text(supported_auth_schemes) AS schemes(scheme)
        WHERE scheme NOT IN ('api_key_header', 'bearer_token')) THEN
        RAISE EXCEPTION 'V26 cannot migrate Integration rows with an unsupported destination authentication scheme; repair them before retrying';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM integrations
        WHERE direction = 'source' AND jsonb_array_length(supported_auth_schemes) > 0) THEN
        RAISE EXCEPTION 'V26 cannot migrate a source-only Integration with destination authentication schemes; repair it before retrying';
    END IF;
END;
$$;

UPDATE integrations
SET manifest = jsonb_build_object(
    'manifest_schema_version', 1,
    'key', key,
    'contract_version', 1,
    'direction', direction,
    'source_configuration_schema', CASE
        WHEN direction IN ('source', 'both')
            THEN jsonb_build_object('type', 'object', 'properties', '{}'::jsonb, 'additionalProperties', true)
        ELSE NULL
    END,
    'destination_configuration_schema', CASE
        WHEN direction IN ('destination', 'both')
            THEN jsonb_build_object('type', 'object', 'properties', '{}'::jsonb, 'additionalProperties', true)
        ELSE NULL
    END,
    'source_verification_schemes', '[]'::jsonb,
    'destination_authentication_schemes', COALESCE((
        SELECT jsonb_agg(jsonb_build_object(
            'scheme', scheme,
            'required_config', CASE
                WHEN scheme = 'api_key_header' THEN '["header_name"]'::jsonb
                ELSE '[]'::jsonb
            END,
            'required_secret_refs', CASE
                WHEN scheme = 'api_key_header' THEN '["api_key"]'::jsonb
                WHEN scheme = 'bearer_token' THEN '["token"]'::jsonb
                ELSE '[]'::jsonb
            END) ORDER BY scheme)
        FROM jsonb_array_elements_text(integrations.supported_auth_schemes) AS schemes(scheme)
    ), '[]'::jsonb),
    'presentation', jsonb_build_object(
        'name', name,
        'description', description,
        'event_types', '[]'::jsonb,
        'authoring_presets', '[]'::jsonb)
);

UPDATE integrations
SET manifest = manifest - 'source_configuration_schema'
WHERE direction = 'destination';

UPDATE integrations
SET manifest = manifest - 'destination_configuration_schema'
WHERE direction = 'source';

ALTER TABLE integrations
    ALTER COLUMN manifest SET NOT NULL,
    ALTER COLUMN contract_version DROP DEFAULT,
    ALTER COLUMN manifest_schema_version DROP DEFAULT,
    ADD CONSTRAINT ck_integrations_contract_version_positive CHECK (contract_version > 0),
    ADD CONSTRAINT ck_integrations_manifest_schema_version_positive CHECK (manifest_schema_version > 0),
    ADD CONSTRAINT ck_integrations_manifest_object CHECK (jsonb_typeof(manifest) = 'object'),
    ADD CONSTRAINT ck_integrations_manifest_identity CHECK (
        manifest->>'key' = key
        AND (manifest->>'contract_version')::INTEGER = contract_version
        AND (manifest->>'manifest_schema_version')::INTEGER = manifest_schema_version);

ALTER TABLE integrations
    DROP CONSTRAINT integrations_key_key;

ALTER TABLE integrations
    ADD CONSTRAINT uq_integrations_key_contract_version UNIQUE (key, contract_version);

CREATE FUNCTION reject_integration_functional_update()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.id IS DISTINCT FROM OLD.id
       OR NEW.key IS DISTINCT FROM OLD.key
       OR NEW.contract_version IS DISTINCT FROM OLD.contract_version
       OR NEW.manifest_schema_version IS DISTINCT FROM OLD.manifest_schema_version
       OR NEW.direction IS DISTINCT FROM OLD.direction
       OR NEW.supported_auth_schemes IS DISTINCT FROM OLD.supported_auth_schemes
       OR (NEW.manifest - 'presentation') IS DISTINCT FROM (OLD.manifest - 'presentation') THEN
        RAISE EXCEPTION 'Integration functional contracts are immutable; apply a new contract_version';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER integrations_reject_functional_update
BEFORE UPDATE ON integrations
FOR EACH ROW
EXECUTE FUNCTION reject_integration_functional_update();
