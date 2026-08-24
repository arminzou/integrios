using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    public partial class EventDeliveryVocabulary : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_events_require_active_topic_source ON events;
                DROP FUNCTION IF EXISTS events_require_active_topic_source();
                ALTER TABLE events DROP CONSTRAINT IF EXISTS ck_events_source_connection_required;
                """);

            migrationBuilder.DropForeignKey("delivery_attempts_subscription_delivery_id_fkey", "delivery_attempts");
            migrationBuilder.DropForeignKey("fk_events_source_connection_tenant", "events");
            migrationBuilder.DropForeignKey("fk_events_topic_source", "events");
            migrationBuilder.Sql("""
                INSERT INTO sources (id, tenant_id, connection_id, topic_id, type, configuration, status, created_at, updated_at)
                SELECT gen_random_uuid(), e.tenant_id, e.source_connection_id, e.topic_id, 'event_api', '{}'::jsonb, 'active', now(), now()
                FROM (SELECT DISTINCT tenant_id, topic_id, source_connection_id FROM events) e
                WHERE NOT EXISTS (
                    SELECT 1 FROM sources s
                    WHERE s.tenant_id = e.tenant_id AND s.connection_id = e.source_connection_id AND s.topic_id = e.topic_id);

                UPDATE events e SET source_connection_id = s.id
                FROM sources s
                WHERE s.tenant_id = e.tenant_id AND s.connection_id = e.source_connection_id AND s.topic_id = e.topic_id;
                """);
            migrationBuilder.RenameColumn("source_connection_id", "events", "source_id");
            migrationBuilder.RenameColumn("transform_config", "subscriptions", "mapping_config");
            migrationBuilder.RenameColumn("transform_config_snapshot", "subscription_deliveries", "mapping_config_snapshot");
            migrationBuilder.RenameColumn("subscription_delivery_id", "delivery_attempts", "event_delivery_id");
            migrationBuilder.RenameTable("subscription_deliveries", newName: "event_deliveries");
            migrationBuilder.RenameIndex(name: "idx_subscription_deliveries_claimable", table: "event_deliveries", newName: "idx_event_deliveries_claimable");
            migrationBuilder.RenameIndex(name: "idx_subscription_deliveries_event_id", table: "event_deliveries", newName: "idx_event_deliveries_event_id");
            migrationBuilder.RenameIndex(name: "idx_subscription_deliveries_subscription_id", table: "event_deliveries", newName: "idx_event_deliveries_subscription_id");

            migrationBuilder.Sql("ALTER TABLE events ADD CONSTRAINT ck_events_source_required CHECK (source_id IS NOT NULL);");
            migrationBuilder.AddForeignKey("delivery_attempts_event_delivery_id_fkey", "delivery_attempts", "event_delivery_id", "event_deliveries", principalColumn: "id");
            migrationBuilder.AddForeignKey("fk_events_source_tenant", "events", columns: new[] { "tenant_id", "source_id" }, "sources", principalColumns: new[] { "tenant_id", "id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey("delivery_attempts_event_delivery_id_fkey", "delivery_attempts");
            migrationBuilder.DropForeignKey("fk_events_source_tenant", "events");
            migrationBuilder.Sql("""
                UPDATE events e SET source_id = s.connection_id
                FROM sources s
                WHERE s.tenant_id = e.tenant_id AND s.id = e.source_id;
                """);
            migrationBuilder.Sql("ALTER TABLE events DROP CONSTRAINT IF EXISTS ck_events_source_required;");
            migrationBuilder.RenameIndex(name: "idx_event_deliveries_claimable", table: "event_deliveries", newName: "idx_subscription_deliveries_claimable");
            migrationBuilder.RenameIndex(name: "idx_event_deliveries_event_id", table: "event_deliveries", newName: "idx_subscription_deliveries_event_id");
            migrationBuilder.RenameIndex(name: "idx_event_deliveries_subscription_id", table: "event_deliveries", newName: "idx_subscription_deliveries_subscription_id");
            migrationBuilder.RenameTable("event_deliveries", newName: "subscription_deliveries");
            migrationBuilder.RenameColumn("event_delivery_id", "delivery_attempts", "subscription_delivery_id");
            migrationBuilder.RenameColumn("mapping_config_snapshot", "subscription_deliveries", "transform_config_snapshot");
            migrationBuilder.RenameColumn("mapping_config", "subscriptions", "transform_config");
            migrationBuilder.RenameColumn("source_id", "events", "source_connection_id");
            migrationBuilder.AddForeignKey("delivery_attempts_subscription_delivery_id_fkey", "delivery_attempts", "subscription_delivery_id", "subscription_deliveries", principalColumn: "id");
            migrationBuilder.AddForeignKey("fk_events_source_connection_tenant", "events", columns: new[] { "tenant_id", "source_connection_id" }, "connections", principalColumns: new[] { "tenant_id", "id" });
            migrationBuilder.AddForeignKey("fk_events_topic_source", "events", columns: new[] { "tenant_id", "topic_id", "source_connection_id" }, "topic_sources", principalColumns: new[] { "tenant_id", "topic_id", "connection_id" });
        }
    }
}
