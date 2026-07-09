-- Migrations are schema-only going forward: platform-owned catalog rows (built-in Integrations)
-- and the first global AdminKey are now created by the `Integrios.Admin bootstrap` command, not
-- migrations. V4 and V8 stay unchanged as history for already-applied databases (Flyway
-- checksums); this migration removes the rows they seeded so bootstrap is the sole creator
-- going forward. Ship the bootstrap command in the same deploy as this migration — applying it
-- alone, with no bootstrap step configured, leaves a fresh database with no webhook integration
-- and no global admin key until `bootstrap dev` (or `bootstrap builtins`/`admin-key`) is run.
--
-- The integrations delete below intentionally fails this migration (FK RESTRICT via
-- connections.integration_id) on an already-migrated database that still has a connection
-- pointing at the built-in webhook integration — that's the enforcement mechanism behind
-- "ship bootstrap in the same deploy," not a bug to work around.

DELETE FROM admin_keys WHERE tenant_id IS NULL AND public_key = 'global_admin_key';

DELETE FROM integrations WHERE id = '00000000-0000-0000-0000-000000000001' AND key = 'webhook';
