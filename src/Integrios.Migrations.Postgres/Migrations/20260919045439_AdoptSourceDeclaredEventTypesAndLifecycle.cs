using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AdoptSourceDeclaredEventTypesAndLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "uq_topics_tenant_key",
                table: "topics");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_revoked_at",
                table: "sources");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_status",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "status",
                table: "topics");

            migrationBuilder.DropColumn(
                name: "status",
                table: "tenant_api_keys");

            migrationBuilder.DropColumn(
                name: "match_rules",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "status",
                table: "connectors");

            // revoked_at and deleted_at mean different things: a revoked Source becomes Inactive
            // below rather than a deleted tombstone, so the column is replaced, not renamed.
            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "sources");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "topics",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "subscriptions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValueSql: "'active'::text");

            migrationBuilder.AlterColumn<string>(
                name: "http_delivery",
                table: "subscriptions",
                type: "jsonb",
                nullable: true,
                defaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_types",
                table: "subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "sources",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValueSql: "'active'::text");

            migrationBuilder.AddColumn<string>(
                name: "event_types",
                table: "sources",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "destinations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValueSql: "'active'::text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "destinations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "uq_topics_tenant_key",
                table: "topics",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "(deleted_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_event_types_array",
                table: "subscriptions",
                sql: "jsonb_typeof(event_types) = 'array'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_event_types_array",
                table: "sources",
                sql: "jsonb_typeof(event_types) = 'array'");

            // Operational status is Active or Inactive. A revoked Source becomes Inactive, the nearest
            // reversible state; Disabled Destinations, Subscriptions, and Tenants become Inactive.
            migrationBuilder.Sql("UPDATE sources SET status = CASE WHEN status = 'active' THEN 'active' ELSE 'inactive' END;");
            migrationBuilder.Sql("UPDATE destinations SET status = 'inactive' WHERE status = 'disabled';");
            migrationBuilder.Sql("UPDATE subscriptions SET status = 'inactive' WHERE status = 'disabled';");
            migrationBuilder.Sql("UPDATE tenants SET status = 'inactive' WHERE status = 'disabled';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_status",
                table: "sources",
                sql: "status IN ('active', 'inactive')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_topics_tenant_key",
                table: "topics");

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_event_types_array",
                table: "subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_event_types_array",
                table: "sources");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_status",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "topics");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "event_types",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "event_types",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "destinations");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "sources");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revoked_at",
                table: "sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "topics",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "tenant_api_keys",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "subscriptions",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "http_delivery",
                table: "subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true,
                oldDefaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb");

            migrationBuilder.AddColumn<JsonElement>(
                name: "match_rules",
                table: "subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "sources",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "destinations",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "connectors",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text");

            migrationBuilder.AddUniqueConstraint(
                name: "uq_topics_tenant_key",
                table: "topics",
                columns: new[] { "tenant_id", "key" });

            // The previous lookup authenticated on status alone, so a revoked key must come back
            // disabled rather than taking the column default. Sources return as active because the
            // previous model has no reversible inactive state for them.
            migrationBuilder.Sql("UPDATE tenant_api_keys SET status = 'disabled' WHERE revoked_at IS NOT NULL;");
            migrationBuilder.Sql("UPDATE sources SET status = 'active';");
            migrationBuilder.Sql("UPDATE destinations SET status = 'disabled' WHERE status = 'inactive';");
            migrationBuilder.Sql("UPDATE subscriptions SET status = 'disabled' WHERE status = 'inactive';");
            migrationBuilder.Sql("UPDATE tenants SET status = 'disabled' WHERE status = 'inactive';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_revoked_at",
                table: "sources",
                sql: "((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_status",
                table: "sources",
                sql: "status IN ('active', 'revoked')");
        }
    }
}
