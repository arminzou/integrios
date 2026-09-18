using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceSubscriptionMatchRulesWithEventTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_match_rules_json",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "match_rules",
                table: "subscriptions");

            migrationBuilder.AddColumn<string>(
                name: "event_types",
                table: "subscriptions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_event_types_array",
                table: "subscriptions",
                sql: "ISJSON(event_types, ARRAY) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_event_types_array",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "event_types",
                table: "subscriptions");

            migrationBuilder.AddColumn<string>(
                name: "match_rules",
                table: "subscriptions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "N'{}'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_match_rules_json",
                table: "subscriptions",
                sql: "ISJSON(match_rules, VALUE) = 1");
        }
    }
}
