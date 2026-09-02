using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantEventHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_events_tenant_id",
                table: "events");

            migrationBuilder.CreateIndex(
                name: "idx_outbox_event_id",
                table: "outbox",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "idx_events_tenant_accepted",
                table: "events",
                columns: new[] { "tenant_id", "accepted_at", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_outbox_event_id",
                table: "outbox");

            migrationBuilder.DropIndex(
                name: "idx_events_tenant_accepted",
                table: "events");

            migrationBuilder.CreateIndex(
                name: "idx_events_tenant_id",
                table: "events",
                column: "tenant_id");
        }
    }
}
