using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RenameSourceTypeQueueToBroker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The stored value comes from the enum member name through SnakeCaseEnumConverter, so EF
            // sees no schema diff for the rename itself. The constraint is dropped before the update
            // so a broker row is never validated against the queue-only predicate, and re-added after.
            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_type",
                table: "sources");

            migrationBuilder.Sql("UPDATE sources SET type = 'broker' WHERE type = 'queue';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_type",
                table: "sources",
                sql: "type IN ('event_api', 'webhook', 'broker')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_type",
                table: "sources");

            migrationBuilder.Sql("UPDATE sources SET type = 'queue' WHERE type = 'broker';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_type",
                table: "sources",
                sql: "type IN ('event_api', 'webhook', 'queue')");
        }
    }
}
