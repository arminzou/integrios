using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceSubscriptionMatchRulesWithEventTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "match_rules",
                table: "subscriptions");

            migrationBuilder.AddColumn<string>(
                name: "event_types",
                table: "subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_event_types_array",
                table: "subscriptions",
                sql: "jsonb_typeof(event_types) = 'array'");
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

            migrationBuilder.AddColumn<JsonElement>(
                name: "match_rules",
                table: "subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }
    }
}
