using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEventsTopicTypeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_events_topic_type_accepted",
                table: "events",
                columns: new[] { "tenant_id", "topic_id", "event_type", "accepted_at", "id" },
                descending: new[] { false, false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_events_topic_type_accepted",
                table: "events");
        }
    }
}
