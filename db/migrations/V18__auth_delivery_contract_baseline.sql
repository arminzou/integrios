ALTER TABLE integrations
    ADD COLUMN supported_auth_schemes JSONB NOT NULL DEFAULT '[]'::jsonb;

UPDATE integrations
SET supported_auth_schemes = CASE
    WHEN auth_scheme = 'none' THEN '[]'::jsonb
    ELSE jsonb_build_array(auth_scheme)
END;

ALTER TABLE integrations
    DROP COLUMN auth_scheme;

ALTER TABLE connections
    ADD COLUMN auth JSONB NULL;

ALTER TABLE connections
    DROP COLUMN secret_refs;
