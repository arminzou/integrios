using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "password_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    normalized_email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, collation: "Latin1_General_100_BIN2"),
                    password_hash = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    session_revision = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    disabled_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("password_credentials_pkey", x => x.id);
                    table.CheckConstraint("ck_password_credentials_session_revision_positive", "session_revision > 0");
                    table.ForeignKey(
                        name: "password_credentials_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_password_credentials_normalized_email",
                table: "password_credentials",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_password_credentials_user_id",
                table: "password_credentials",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_credentials");
        }
    }
}
