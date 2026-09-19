using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AdoptOperationalStatusVocabulary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_status",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "status",
                table: "tenant_api_keys");

            migrationBuilder.DropColumn(
                name: "status",
                table: "connectors");

            // The single OperationalStatus is Active/Inactive. A Tenant's one-way Active/Disabled
            // becomes reversible, and Source/Destination/Subscription lose the Enabled/Disabled
            // vocabulary. Rewrite the stored spellings before the check returns.
            migrationBuilder.Sql("UPDATE tenants SET status = 'inactive' WHERE status = 'disabled';");
            migrationBuilder.Sql("UPDATE sources SET status = CASE WHEN status = 'enabled' THEN 'active' ELSE 'inactive' END;");
            migrationBuilder.Sql("UPDATE destinations SET status = CASE WHEN status = 'enabled' THEN 'active' ELSE 'inactive' END;");
            migrationBuilder.Sql("UPDATE subscriptions SET status = CASE WHEN status = 'enabled' THEN 'active' ELSE 'inactive' END;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_status",
                table: "sources",
                sql: "status IN ('active', 'inactive')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_status",
                table: "sources");

            migrationBuilder.Sql("UPDATE tenants SET status = 'disabled' WHERE status = 'inactive';");
            migrationBuilder.Sql("UPDATE sources SET status = CASE WHEN status = 'active' THEN 'enabled' ELSE 'disabled' END;");
            migrationBuilder.Sql("UPDATE destinations SET status = CASE WHEN status = 'active' THEN 'enabled' ELSE 'disabled' END;");
            migrationBuilder.Sql("UPDATE subscriptions SET status = CASE WHEN status = 'active' THEN 'enabled' ELSE 'disabled' END;");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "tenant_api_keys",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text");

            // The pre-change lookup authenticated on status alone, so a revoked key must come back
            // disabled rather than taking the column default.
            migrationBuilder.Sql("UPDATE tenant_api_keys SET status = 'disabled' WHERE revoked_at IS NOT NULL;");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "connectors",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_status",
                table: "sources",
                sql: "status IN ('enabled', 'disabled')");
        }
    }
}
