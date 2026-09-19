using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEventsTenantBacklogIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_events_tenant_backlog",
                table: "events",
                columns: new[] { "tenant_id", "accepted_at" },
                filter: "(status IN ('accepted', 'unrouted'))")
                .Annotation("SqlServer:Include", new[] { "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_events_tenant_backlog",
                table: "events");
        }
    }
}
