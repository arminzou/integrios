using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEventDeliveryTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM connectors WHERE key = 'subscriber') THEN
                        RAISE EXCEPTION 'Connector key ''subscriber'' is reserved for the built-in pull Connector, but an authored Connector already uses it. Resolve that Connector before migrating.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "idx_event_deliveries_claimable",
                table: "event_deliveries");

            migrationBuilder.AlterColumn<JsonElement>(
                name: "http_execution_snapshot",
                table: "event_deliveries",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(JsonElement),
                oldType: "jsonb");

            migrationBuilder.AddColumn<string>(
                name: "transport",
                table: "event_deliveries",
                type: "text",
                nullable: false,
                defaultValueSql: "'http'::text");

            migrationBuilder.CreateIndex(
                name: "idx_event_deliveries_claimable",
                table: "event_deliveries",
                columns: new[] { "transport", "status", "lease_expires_at", "deliver_after", "created_at" },
                filter: "(status = ANY (ARRAY['pending'::text, 'in_flight'::text]))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_event_deliveries_transport_snapshot",
                table: "event_deliveries",
                sql: "((transport = 'http' AND http_execution_snapshot IS NOT NULL) OR (transport = 'pull' AND http_execution_snapshot IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_event_deliveries_claimable",
                table: "event_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_event_deliveries_transport_snapshot",
                table: "event_deliveries");

            migrationBuilder.DropColumn(
                name: "transport",
                table: "event_deliveries");

            migrationBuilder.AlterColumn<JsonElement>(
                name: "http_execution_snapshot",
                table: "event_deliveries",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(JsonElement),
                oldType: "jsonb",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_event_deliveries_claimable",
                table: "event_deliveries",
                columns: new[] { "status", "lease_expires_at", "deliver_after", "created_at" },
                filter: "(status = ANY (ARRAY['pending'::text, 'in_flight'::text]))");
        }
    }
}
