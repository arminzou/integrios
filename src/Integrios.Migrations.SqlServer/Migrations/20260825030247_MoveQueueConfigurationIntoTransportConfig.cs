using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
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
            // Rows already carrying transport_config are untouched. Every read of configuration on
            // the right-hand side sees the pre-update value, so one expression can both copy the old
            // keys in and delete them; JSON_MODIFY with NULL is what removes a key, which also drops
            // queue_name from transport_config when the old row had none.
            migrationBuilder.Sql(
                """
                UPDATE sources
                SET configuration =
                    JSON_MODIFY(
                        JSON_MODIFY(
                            JSON_MODIFY(
                                JSON_MODIFY(
                                    JSON_MODIFY(configuration, '$.transport_config', JSON_QUERY('{}')),
                                    '$.transport_config.namespace',
                                    JSON_VALUE(configuration, '$.namespace')),
                                '$.transport_config.queue_name',
                                JSON_VALUE(configuration, '$.queue_name')),
                            '$.namespace',
                            NULL),
                        '$.queue_name',
                        NULL)
                WHERE type = N'queue'
                  AND JSON_VALUE(configuration, '$.namespace') IS NOT NULL
                  AND JSON_QUERY(configuration, '$.transport_config') IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE sources
                SET configuration =
                    JSON_MODIFY(
                        JSON_MODIFY(
                            JSON_MODIFY(
                                configuration,
                                '$.namespace',
                                JSON_VALUE(configuration, '$.transport_config.namespace')),
                            '$.queue_name',
                            JSON_VALUE(configuration, '$.transport_config.queue_name')),
                        '$.transport_config',
                        NULL)
                WHERE type = N'queue'
                  AND JSON_QUERY(configuration, '$.transport_config') IS NOT NULL;
                """);
        }
    }
}
