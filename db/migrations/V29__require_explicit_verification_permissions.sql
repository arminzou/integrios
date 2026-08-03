DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN SELECT id FROM integrations
        WHERE NOT (manifest ? 'source_verification_schemes')
           OR NOT (manifest ? 'destination_authentication_schemes')
    LOOP
        RAISE EXCEPTION 'V29 found Integration % missing the legacy scheme arrays it expects to migrate', r.id;
    END LOOP;
END
$$;

ALTER TABLE integrations DISABLE TRIGGER integrations_reject_functional_update;

UPDATE integrations
SET manifest = (manifest - 'source_verification_schemes' - 'destination_authentication_schemes')
    || jsonb_build_object(
        'source_verification', jsonb_build_object(
            'allow_unverified', jsonb_array_length(manifest->'source_verification_schemes') = 0,
            'schemes', manifest->'source_verification_schemes'),
        'destination_authentication', jsonb_build_object(
            'allow_unauthenticated', jsonb_array_length(manifest->'destination_authentication_schemes') = 0,
            'schemes', manifest->'destination_authentication_schemes'));

ALTER TABLE integrations ENABLE TRIGGER integrations_reject_functional_update;
