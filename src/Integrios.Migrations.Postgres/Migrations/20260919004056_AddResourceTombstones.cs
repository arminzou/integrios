using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceTombstones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "topics",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "http_delivery",
                table: "subscriptions",
                type: "jsonb",
                nullable: true,
                defaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "destinations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "topics");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "destinations");

            migrationBuilder.AlterColumn<string>(
                name: "http_delivery",
                table: "subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true,
                oldDefaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb");
        }
    }
}
