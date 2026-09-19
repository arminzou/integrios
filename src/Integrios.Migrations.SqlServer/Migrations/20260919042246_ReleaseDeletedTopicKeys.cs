using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseDeletedTopicKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "uq_topics_tenant_key",
                table: "topics");

            migrationBuilder.CreateIndex(
                name: "uq_topics_tenant_key",
                table: "topics",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "(deleted_at IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_topics_tenant_key",
                table: "topics");

            migrationBuilder.AddUniqueConstraint(
                name: "uq_topics_tenant_key",
                table: "topics",
                columns: new[] { "tenant_id", "key" });
        }
    }
}
