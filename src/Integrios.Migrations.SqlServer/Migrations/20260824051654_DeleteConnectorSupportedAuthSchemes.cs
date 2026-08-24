using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class DeleteConnectorSupportedAuthSchemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_connectors_supported_auth_schemes_json",
                table: "connectors");

            // The immutability trigger from SqlServerBaseline compares supported_auth_schemes
            // directly; the destination-authentication schemes it guarded now live only inside
            // manifest, whose own JSON_MODIFY comparison below already covers them.
            migrationBuilder.Sql(
                """
                ALTER TRIGGER connectors_reject_functional_update
                ON connectors
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM inserted
                        EXCEPT
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM deleted
                    ) OR EXISTS (
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM deleted
                        EXCEPT
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM inserted
                    )
                        THROW 51000, 'Connector functional contracts are immutable; apply a new contract_version', 1;
                END
                """);

            migrationBuilder.DropColumn(
                name: "supported_auth_schemes",
                table: "connectors");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "supported_auth_schemes",
                table: "connectors",
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "N'[]'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connectors_supported_auth_schemes_json",
                table: "connectors",
                sql: "ISJSON(supported_auth_schemes, VALUE) = 1");

            migrationBuilder.Sql(
                """
                ALTER TRIGGER connectors_reject_functional_update
                ON connectors
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               supported_auth_schemes, JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM inserted
                        EXCEPT
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               supported_auth_schemes, JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM deleted
                    ) OR EXISTS (
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               supported_auth_schemes, JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM deleted
                        EXCEPT
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               supported_auth_schemes, JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM inserted
                    )
                        THROW 51000, 'Connector functional contracts are immutable; apply a new contract_version', 1;
                END
                """);
        }
    }
}
