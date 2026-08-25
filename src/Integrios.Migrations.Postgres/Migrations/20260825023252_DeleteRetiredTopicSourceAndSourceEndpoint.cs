using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DeleteRetiredTopicSourceAndSourceEndpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_endpoints");

            migrationBuilder.DropTable(
                name: "topic_sources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "topic_sources",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    inactive_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text")
                },
                constraints: table =>
                {
                    table.PrimaryKey("topic_sources_pkey", x => new { x.tenant_id, x.topic_id, x.connection_id });
                    table.CheckConstraint("ck_topic_sources_inactive_at", "((status = 'active' AND inactive_at IS NULL) OR (status = 'inactive' AND inactive_at IS NOT NULL))");
                    table.CheckConstraint("ck_topic_sources_status", "status IN ('active', 'inactive')");
                    table.ForeignKey(
                        name: "fk_topic_sources_connection_tenant",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" });
                    table.ForeignKey(
                        name: "fk_topic_sources_topic_tenant",
                        columns: x => new { x.tenant_id, x.topic_id },
                        principalTable: "topics",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    callback_path = table.Column<string>(type: "text", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("source_endpoints_pkey", x => x.id);
                    table.UniqueConstraint("source_endpoints_callback_path_key", x => x.callback_path);
                    table.CheckConstraint("ck_source_endpoints_revoked_at", "((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL))");
                    table.CheckConstraint("ck_source_endpoints_status", "status IN ('active', 'revoked')");
                    table.ForeignKey(
                        name: "fk_source_endpoints_topic_source",
                        columns: x => new { x.tenant_id, x.topic_id, x.connection_id },
                        principalTable: "topic_sources",
                        principalColumns: new[] { "tenant_id", "topic_id", "connection_id" });
                });

            migrationBuilder.CreateIndex(
                name: "idx_source_endpoints_association",
                table: "source_endpoints",
                columns: new[] { "tenant_id", "topic_id", "connection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_source_endpoints_active_association",
                table: "source_endpoints",
                columns: new[] { "tenant_id", "topic_id", "connection_id" },
                unique: true,
                filter: "(status = 'active'::text)");

            migrationBuilder.CreateIndex(
                name: "idx_topic_sources_connection_id",
                table: "topic_sources",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "idx_topic_sources_topic_id",
                table: "topic_sources",
                column: "topic_id");
        }
    }
}
