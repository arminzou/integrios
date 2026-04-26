CREATE TABLE admin_keys (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   UUID REFERENCES tenants(id),  -- NULL = global, set = tenant-scoped
    public_key  TEXT NOT NULL UNIQUE,
    secret_hash TEXT NOT NULL,
    name        TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at  TIMESTAMPTZ
);

CREATE INDEX idx_admin_keys_lookup ON admin_keys(public_key) WHERE revoked_at IS NULL;
CREATE INDEX idx_admin_keys_tenant_id ON admin_keys(tenant_id) WHERE tenant_id IS NOT NULL;

-- Dev bootstrap: global admin key (rotate before any real deployment)
-- Authorization: AdminKey global_admin_key:admin_bootstrap_secret
INSERT INTO admin_keys (tenant_id, public_key, secret_hash, name)
VALUES (
    NULL,
    'global_admin_key',
    'sha256:5af35a0149f5a07231b181c3b4d5d3a76a4c765258533a123b34dfb843599328',
    'Bootstrap Global Admin Key'
);
