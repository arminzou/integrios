-- Migrations are schema-only going forward: platform-owned catalog rows (built-in Integrations)
-- and the first global AdminKey are now created by the `Integrios.Admin bootstrap` command, not
-- migrations. V4 and V8 stay unchanged as history for already-applied databases (Flyway
-- checksums); this migration removes the rows they seeded so bootstrap is the sole creator
-- going forward. Ship the bootstrap command in the same deploy as this migration — applying it
-- alone, with no bootstrap step configured, leaves a fresh database with no webhook integration
-- and no global admin key until `bootstrap` is run.
--
-- Used databases remain upgradeable: an Operator-replaced AdminKey is not V8's seed row, and a
-- referenced built-in Integration must remain in place for Bootstrap to reconcile after migrate.

DELETE FROM admin_keys
WHERE tenant_id IS NULL
  AND public_key = 'global_admin_key'
  AND secret_hash = 'sha256:5af35a0149f5a07231b181c3b4d5d3a76a4c765258533a123b34dfb843599328'
  AND name = 'Bootstrap Global Admin Key'
  AND revoked_at IS NULL;

DELETE FROM integrations AS integration
WHERE integration.id = '00000000-0000-0000-0000-000000000001'
  AND integration.key = 'webhook'
  AND NOT EXISTS (
      SELECT 1
      FROM connections
      WHERE connections.integration_id = integration.id
  );
