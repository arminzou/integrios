using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuthAuthenticationFailurePhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_delivery_attempts_failure_phase",
                table: "delivery_attempts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_delivery_attempts_failure_phase",
                table: "delivery_attempts",
                sql: "((status = 'failed' AND failure_phase IS NOT NULL AND failure_phase IN ('transform', 'secret_resolution', 'authentication', 'request_construction', 'http')) OR (status <> 'failed' AND failure_phase IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_delivery_attempts_failure_phase",
                table: "delivery_attempts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_delivery_attempts_failure_phase",
                table: "delivery_attempts",
                sql: "((status = 'failed' AND failure_phase IS NOT NULL AND failure_phase IN ('transform', 'secret_resolution', 'request_construction', 'http')) OR (status <> 'failed' AND failure_phase IS NULL))");
        }
    }
}
