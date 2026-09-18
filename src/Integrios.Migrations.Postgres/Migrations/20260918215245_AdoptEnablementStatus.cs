using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AdoptEnablementStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_revoked_at",
                table: "sources");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_status",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "status",
                table: "topics");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "sources");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "subscriptions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValueSql: "'active'::text");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "sources",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValueSql: "'active'::text");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "destinations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValueSql: "'active'::text");

            // Active meant eligible and stays so; a revoked Source was removed, and Disabled is the
            // nearest reversible state until deletion exists.
            migrationBuilder.Sql("UPDATE sources SET status = CASE WHEN status = 'active' THEN 'enabled' ELSE 'disabled' END;");
            migrationBuilder.Sql("UPDATE destinations SET status = 'enabled' WHERE status = 'active';");
            migrationBuilder.Sql("UPDATE subscriptions SET status = 'enabled' WHERE status = 'active';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_status",
                table: "sources",
                sql: "status IN ('enabled', 'disabled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_status",
                table: "sources");

            migrationBuilder.Sql("UPDATE sources SET status = 'active';");
            migrationBuilder.Sql("UPDATE destinations SET status = 'active' WHERE status = 'enabled';");
            migrationBuilder.Sql("UPDATE subscriptions SET status = 'active' WHERE status = 'enabled';");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "topics",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "subscriptions",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "sources",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revoked_at",
                table: "sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "destinations",
                type: "text",
                nullable: false,
                defaultValueSql: "'active'::text",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_revoked_at",
                table: "sources",
                sql: "((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_status",
                table: "sources",
                sql: "status IN ('active', 'revoked')");
        }
    }
}
