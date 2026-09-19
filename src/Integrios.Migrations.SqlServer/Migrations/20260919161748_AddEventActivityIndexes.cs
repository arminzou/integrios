using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEventActivityIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_events_tenant_accepted",
                table: "events");

            migrationBuilder.CreateIndex(
                name: "idx_events_tenant_accepted",
                table: "events",
                columns: new[] { "tenant_id", "accepted_at", "id" },
                descending: new[] { false, true, true })
                .Annotation("SqlServer:Include", new[] { "status" });

            migrationBuilder.CreateIndex(
                name: "idx_event_deliveries_dead_lettered",
                table: "event_deliveries",
                column: "event_id",
                filter: "(status = 'dead_lettered')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_events_tenant_accepted",
                table: "events");

            migrationBuilder.DropIndex(
                name: "idx_event_deliveries_dead_lettered",
                table: "event_deliveries");

            migrationBuilder.CreateIndex(
                name: "idx_events_tenant_accepted",
                table: "events",
                columns: new[] { "tenant_id", "accepted_at", "id" },
                descending: new[] { false, true, true });
        }
    }
}
