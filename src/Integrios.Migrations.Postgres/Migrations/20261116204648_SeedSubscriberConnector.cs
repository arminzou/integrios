using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SeedSubscriberConnector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM connectors WHERE key = 'subscriber') THEN
                        RAISE EXCEPTION 'Connector key ''subscriber'' is reserved for the built-in pull Connector, but an authored Connector already uses it. Resolve that Connector before migrating.';
                    END IF;
                END $$;
                """);

            migrationBuilder.InsertData(
                table: "connectors",
                columns: new[] { "id", "contract_version", "created_at", "description", "direction", "key", "manifest", "manifest_schema_version", "name", "updated_at" },
                values: new object[] { new Guid("5ab5c71b-e000-4000-8000-000000000001"), 1, new DateTimeOffset(new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "A Subscriber service claims deliveries for this Destination and reports each outcome, instead of receiving HTTP requests.", "destination", "subscriber", "{\"manifest_schema_version\":1,\"key\":\"subscriber\",\"contract_version\":1,\"direction\":\"destination\",\"destination_configuration_schema\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false},\"source_verification\":{\"allow_unverified\":true,\"schemes\":[]},\"destination_authentication\":{\"allow_unauthenticated\":true,\"schemes\":[]},\"presentation\":{\"name\":\"Subscriber\",\"description\":\"A Subscriber service claims deliveries for this Destination and reports each outcome, instead of receiving HTTP requests.\",\"event_types\":[],\"authoring_presets\":[]}}", 1, "Subscriber", new DateTimeOffset(new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "connectors",
                keyColumn: "id",
                keyValue: new Guid("5ab5c71b-e000-4000-8000-000000000001"));
        }
    }
}
