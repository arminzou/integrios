DO $$
DECLARE
    invalid_tenant_ids TEXT;
BEGIN
    SELECT string_agg(id::text, ', ' ORDER BY id)
    INTO invalid_tenant_ids
    FROM tenants
    WHERE slug !~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$';

    IF invalid_tenant_ids IS NOT NULL THEN
        RAISE EXCEPTION
            'V22 requires lowercase DNS-label tenant slugs before migration. Invalid tenant IDs: %. Rename each slug and move or map its external secret namespace before retrying.',
            invalid_tenant_ids;
    END IF;
END $$;

ALTER TABLE tenants
    ADD CONSTRAINT chk_tenants_slug_dns_label
    CHECK (slug ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$');
