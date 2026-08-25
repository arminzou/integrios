using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class MoveQueueConfigurationIntoTransportConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Queue Sources moved their transport-specific settings out of the top level and into a
            // nested transport_config object. A row left in the old shape resolves to no broker
            // entity, so its processor is never built while Admin still reports the Source active.
            // Rows already carrying transport_config are untouched.
            migrationBuilder.Sql(
                """
                UPDATE sources
                SET configuration =
                    (configuration - 'namespace' - 'queue_name')
                    || jsonb_build_object(
                        'transport_config',
                        jsonb_strip_nulls(jsonb_build_object(
                            'namespace', configuration -> 'namespace',
                            'queue_name', configuration -> 'queue_name')))
                WHERE type = 'queue'
                  AND configuration ? 'namespace'
                  AND NOT (configuration ? 'transport_config');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE sources
                SET configuration =
                    (configuration - 'transport_config')
                    || jsonb_strip_nulls(jsonb_build_object(
                        'namespace', configuration -> 'transport_config' -> 'namespace',
                        'queue_name', configuration -> 'transport_config' -> 'queue_name'))
                WHERE type = 'queue'
                  AND configuration ? 'transport_config';
                """);
        }
    }
}
