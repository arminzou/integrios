using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DeleteConnectorSupportedAuthSchemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The immutability trigger from PostgresBaseline compares supported_auth_schemes
            // directly; the destination-authentication schemes it guarded now live only inside
            // manifest, whose own comparison below already covers them.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION reject_connector_functional_update()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.key IS DISTINCT FROM OLD.key
                       OR NEW.contract_version IS DISTINCT FROM OLD.contract_version
                       OR NEW.manifest_schema_version IS DISTINCT FROM OLD.manifest_schema_version
                       OR NEW.direction IS DISTINCT FROM OLD.direction
                       OR (NEW.manifest - 'presentation') IS DISTINCT FROM (OLD.manifest - 'presentation') THEN
                        RAISE EXCEPTION 'Connector functional contracts are immutable; apply a new contract_version';
                    END IF;

                    RETURN NEW;
                END;
                $$;
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
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION reject_connector_functional_update()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.key IS DISTINCT FROM OLD.key
                       OR NEW.contract_version IS DISTINCT FROM OLD.contract_version
                       OR NEW.manifest_schema_version IS DISTINCT FROM OLD.manifest_schema_version
                       OR NEW.direction IS DISTINCT FROM OLD.direction
                       OR NEW.supported_auth_schemes IS DISTINCT FROM OLD.supported_auth_schemes
                       OR (NEW.manifest - 'presentation') IS DISTINCT FROM (OLD.manifest - 'presentation') THEN
                        RAISE EXCEPTION 'Connector functional contracts are immutable; apply a new contract_version';
                    END IF;

                    RETURN NEW;
                END;
                $$;
                """);
        }
    }
}
