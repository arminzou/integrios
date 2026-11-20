using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RenameSubscriberConnectorToPull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM connectors WHERE key = 'pull') THEN
                        RAISE EXCEPTION 'Connector key ''pull'' is reserved for the built-in pull Connector, but an authored Connector already uses it. Resolve that Connector before migrating.';
                    END IF;
                END $$;
                """);

            migrationBuilder.UpdateData(
                table: "connectors",
                keyColumn: "id",
                keyValue: new Guid("5ab5c71b-e000-4000-8000-000000000001"),
                columns: new[] { "key", "manifest", "name" },
                values: new object[] { "pull", "{\"manifest_schema_version\":1,\"key\":\"pull\",\"contract_version\":1,\"direction\":\"destination\",\"destination_configuration_schema\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false},\"source_verification\":{\"allow_unverified\":true,\"schemes\":[]},\"destination_authentication\":{\"allow_unauthenticated\":true,\"schemes\":[]},\"presentation\":{\"name\":\"Pull\",\"description\":\"A Subscriber service claims deliveries for this Destination and reports each outcome, instead of receiving HTTP requests.\",\"event_types\":[],\"authoring_presets\":[]}}", "Pull" });

            migrationBuilder.Sql("UPDATE event_deliveries SET connector_key = 'pull' WHERE connector_key = 'subscriber' AND transport = 'pull';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE event_deliveries SET connector_key = 'subscriber' WHERE connector_key = 'pull' AND transport = 'pull';");

            migrationBuilder.UpdateData(
                table: "connectors",
                keyColumn: "id",
                keyValue: new Guid("5ab5c71b-e000-4000-8000-000000000001"),
                columns: new[] { "key", "manifest", "name" },
                values: new object[] { "subscriber", "{\"manifest_schema_version\":1,\"key\":\"subscriber\",\"contract_version\":1,\"direction\":\"destination\",\"destination_configuration_schema\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false},\"source_verification\":{\"allow_unverified\":true,\"schemes\":[]},\"destination_authentication\":{\"allow_unauthenticated\":true,\"schemes\":[]},\"presentation\":{\"name\":\"Subscriber\",\"description\":\"A Subscriber service claims deliveries for this Destination and reports each outcome, instead of receiving HTTP requests.\",\"event_types\":[],\"authoring_presets\":[]}}", "Subscriber" });
        }
    }
}
