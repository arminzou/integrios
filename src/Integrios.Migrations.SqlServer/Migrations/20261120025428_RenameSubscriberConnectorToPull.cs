using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RenameSubscriberConnectorToPull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM connectors WHERE [key] = N'pull')
                    THROW 50000, N'Connector key ''pull'' is reserved for the built-in pull Connector, but an authored Connector already uses it. Resolve that Connector before migrating.', 1;
                """);

            migrationBuilder.UpdateData(
                table: "connectors",
                keyColumn: "id",
                keyValue: new Guid("5ab5c71b-e000-4000-8000-000000000001"),
                columns: new[] { "key", "manifest", "name" },
                values: new object[] { "pull", "{\"manifest_schema_version\":1,\"key\":\"pull\",\"contract_version\":1,\"direction\":\"destination\",\"destination_configuration_schema\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false},\"source_verification\":{\"allow_unverified\":true,\"schemes\":[]},\"destination_authentication\":{\"allow_unauthenticated\":true,\"schemes\":[]},\"presentation\":{\"name\":\"Pull\",\"description\":\"A Subscriber service claims deliveries for this Destination and reports each outcome, instead of receiving HTTP requests.\",\"event_types\":[],\"authoring_presets\":[]}}", "Pull" });

            migrationBuilder.Sql("UPDATE event_deliveries SET connector_key = N'pull' WHERE connector_key = N'subscriber' AND transport = N'pull';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE event_deliveries SET connector_key = N'subscriber' WHERE connector_key = N'pull' AND transport = N'pull';");

            migrationBuilder.UpdateData(
                table: "connectors",
                keyColumn: "id",
                keyValue: new Guid("5ab5c71b-e000-4000-8000-000000000001"),
                columns: new[] { "key", "manifest", "name" },
                values: new object[] { "subscriber", "{\"manifest_schema_version\":1,\"key\":\"subscriber\",\"contract_version\":1,\"direction\":\"destination\",\"destination_configuration_schema\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false},\"source_verification\":{\"allow_unverified\":true,\"schemes\":[]},\"destination_authentication\":{\"allow_unauthenticated\":true,\"schemes\":[]},\"presentation\":{\"name\":\"Subscriber\",\"description\":\"A Subscriber service claims deliveries for this Destination and reports each outcome, instead of receiving HTTP requests.\",\"event_types\":[],\"authoring_presets\":[]}}", "Subscriber" });
        }
    }
}
