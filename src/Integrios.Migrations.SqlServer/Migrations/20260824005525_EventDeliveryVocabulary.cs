using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    public partial class EventDeliveryVocabulary : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS events_require_active_topic_source;");
            migrationBuilder.DropForeignKey("delivery_attempts_subscription_delivery_id_fkey", "delivery_attempts");
            migrationBuilder.DropForeignKey("fk_events_source_connection_tenant", "events");
            migrationBuilder.DropForeignKey("fk_events_topic_source", "events");
            migrationBuilder.DropCheckConstraint("ck_events_source_connection_required", "events");
            migrationBuilder.DropCheckConstraint("ck_subscriptions_transform_config_json", "subscriptions");
            migrationBuilder.DropCheckConstraint("ck_subscription_deliveries_transform_config_snapshot_json", "subscription_deliveries");
            migrationBuilder.Sql("""
                INSERT INTO sources (id, tenant_id, connection_id, topic_id, type, configuration, status, created_at, updated_at)
                SELECT NEWID(), e.tenant_id, e.source_connection_id, e.topic_id, 'event_api', N'{}', 'active', SYSUTCDATETIME(), SYSUTCDATETIME()
                FROM (SELECT DISTINCT tenant_id, topic_id, source_connection_id FROM events) e
                WHERE NOT EXISTS (
                    SELECT 1 FROM sources s
                    WHERE s.tenant_id = e.tenant_id AND s.connection_id = e.source_connection_id AND s.topic_id = e.topic_id);

                UPDATE e SET source_connection_id = s.id
                FROM events e
                JOIN sources s ON s.tenant_id = e.tenant_id AND s.connection_id = e.source_connection_id AND s.topic_id = e.topic_id;
                """);
            migrationBuilder.RenameColumn("source_connection_id", "events", "source_id");
            migrationBuilder.RenameColumn("transform_config", "subscriptions", "mapping_config");
            migrationBuilder.RenameColumn("transform_config_snapshot", "subscription_deliveries", "mapping_config_snapshot");
            migrationBuilder.RenameColumn("subscription_delivery_id", "delivery_attempts", "event_delivery_id");
            migrationBuilder.RenameTable("subscription_deliveries", newName: "event_deliveries");
            migrationBuilder.RenameIndex(name: "idx_subscription_deliveries_claimable", table: "event_deliveries", newName: "idx_event_deliveries_claimable");
            migrationBuilder.RenameIndex(name: "idx_subscription_deliveries_event_id", table: "event_deliveries", newName: "idx_event_deliveries_event_id");
            migrationBuilder.RenameIndex(name: "idx_subscription_deliveries_subscription_id", table: "event_deliveries", newName: "idx_event_deliveries_subscription_id");

            migrationBuilder.AddCheckConstraint("ck_events_source_required", "events", "source_id IS NOT NULL");
            migrationBuilder.AddCheckConstraint("ck_subscriptions_mapping_config_json", "subscriptions", "mapping_config IS NULL OR ISJSON(mapping_config, VALUE) = 1");
            migrationBuilder.AddCheckConstraint("ck_event_deliveries_mapping_config_snapshot_json", "event_deliveries", "mapping_config_snapshot IS NULL OR ISJSON(mapping_config_snapshot, VALUE) = 1");
            migrationBuilder.AddForeignKey("delivery_attempts_event_delivery_id_fkey", "delivery_attempts", "event_delivery_id", "event_deliveries", principalColumn: "id");
            migrationBuilder.AddForeignKey("fk_events_source_tenant", "events", columns: new[] { "tenant_id", "source_id" }, "sources", principalColumns: new[] { "tenant_id", "id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey("delivery_attempts_event_delivery_id_fkey", "delivery_attempts");
            migrationBuilder.DropForeignKey("fk_events_source_tenant", "events");
            migrationBuilder.DropCheckConstraint("ck_events_source_required", "events");
            migrationBuilder.DropCheckConstraint("ck_subscriptions_mapping_config_json", "subscriptions");
            migrationBuilder.DropCheckConstraint("ck_event_deliveries_mapping_config_snapshot_json", "event_deliveries");
            migrationBuilder.Sql("""
                UPDATE e SET source_id = s.connection_id
                FROM events e
                JOIN sources s ON s.tenant_id = e.tenant_id AND s.id = e.source_id;
                """);
            migrationBuilder.RenameIndex(name: "idx_event_deliveries_claimable", table: "event_deliveries", newName: "idx_subscription_deliveries_claimable");
            migrationBuilder.RenameIndex(name: "idx_event_deliveries_event_id", table: "event_deliveries", newName: "idx_subscription_deliveries_event_id");
            migrationBuilder.RenameIndex(name: "idx_event_deliveries_subscription_id", table: "event_deliveries", newName: "idx_subscription_deliveries_subscription_id");
            migrationBuilder.RenameTable("event_deliveries", newName: "subscription_deliveries");
            migrationBuilder.RenameColumn("event_delivery_id", "delivery_attempts", "subscription_delivery_id");
            migrationBuilder.RenameColumn("mapping_config_snapshot", "subscription_deliveries", "transform_config_snapshot");
            migrationBuilder.RenameColumn("mapping_config", "subscriptions", "transform_config");
            migrationBuilder.RenameColumn("source_id", "events", "source_connection_id");
            migrationBuilder.AddCheckConstraint("ck_events_source_connection_required", "events", "source_connection_id IS NOT NULL");
            migrationBuilder.AddCheckConstraint("ck_subscriptions_transform_config_json", "subscriptions", "transform_config IS NULL OR ISJSON(transform_config, VALUE) = 1");
            migrationBuilder.AddCheckConstraint("ck_subscription_deliveries_transform_config_snapshot_json", "subscription_deliveries", "transform_config_snapshot IS NULL OR ISJSON(transform_config_snapshot, VALUE) = 1");
            migrationBuilder.AddForeignKey("delivery_attempts_subscription_delivery_id_fkey", "delivery_attempts", "subscription_delivery_id", "subscription_deliveries", principalColumn: "id");
            migrationBuilder.AddForeignKey("fk_events_source_connection_tenant", "events", columns: new[] { "tenant_id", "source_connection_id" }, "connections", principalColumns: new[] { "tenant_id", "id" });
            migrationBuilder.AddForeignKey("fk_events_topic_source", "events", columns: new[] { "tenant_id", "topic_id", "source_connection_id" }, "topic_sources", principalColumns: new[] { "tenant_id", "topic_id", "connection_id" });
        }
    }
}
