using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEventDeliveryTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM connectors WHERE [key] = 'subscriber')
                    THROW 51000, 'Connector key ''subscriber'' is reserved for the built-in pull Connector, but an authored Connector already uses it. Resolve that Connector before migrating.', 1;
                """);

            migrationBuilder.DropIndex(
                name: "idx_event_deliveries_claimable",
                table: "event_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_event_deliveries_http_execution_snapshot_json",
                table: "event_deliveries");

            migrationBuilder.AlterColumn<string>(
                name: "http_execution_snapshot",
                table: "event_deliveries",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<string>(
                name: "transport",
                table: "event_deliveries",
                type: "nvarchar(450)",
                nullable: false,
                defaultValueSql: "N'http'");

            migrationBuilder.CreateIndex(
                name: "idx_event_deliveries_claimable",
                table: "event_deliveries",
                columns: new[] { "transport", "status", "lease_expires_at", "deliver_after", "created_at" },
                filter: "(status IN (N'pending', N'in_flight'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_event_deliveries_http_execution_snapshot_json",
                table: "event_deliveries",
                sql: "http_execution_snapshot IS NULL OR ISJSON(http_execution_snapshot, VALUE) = 1");

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
                name: "ck_event_deliveries_http_execution_snapshot_json",
                table: "event_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_event_deliveries_transport_snapshot",
                table: "event_deliveries");

            migrationBuilder.DropColumn(
                name: "transport",
                table: "event_deliveries");

            migrationBuilder.AlterColumn<string>(
                name: "http_execution_snapshot",
                table: "event_deliveries",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_event_deliveries_claimable",
                table: "event_deliveries",
                columns: new[] { "status", "lease_expires_at", "deliver_after", "created_at" },
                filter: "(status IN (N'pending', N'in_flight'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_event_deliveries_http_execution_snapshot_json",
                table: "event_deliveries",
                sql: "ISJSON(http_execution_snapshot, VALUE) = 1");
        }
    }
}
