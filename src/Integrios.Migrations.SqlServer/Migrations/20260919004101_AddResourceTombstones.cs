using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceTombstones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_http_delivery_json",
                table: "subscriptions");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "topics",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "http_delivery",
                table: "subscriptions",
                type: "nvarchar(max)",
                nullable: true,
                defaultValueSql: "N'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValueSql: "N'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "subscriptions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "sources",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "destinations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_http_delivery_json",
                table: "subscriptions",
                sql: "http_delivery IS NULL OR ISJSON(http_delivery, VALUE) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_http_delivery_json",
                table: "subscriptions");

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
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "N'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true,
                oldDefaultValueSql: "N'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_http_delivery_json",
                table: "subscriptions",
                sql: "ISJSON(http_delivery, VALUE) = 1");
        }
    }
}
