using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "password_reset_expires_at",
                table: "users",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_reset_token_hash",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Existing users each get their own random stamp (a volatile default is evaluated per
            // row on ADD COLUMN); the default is then dropped - new users get theirs from User.
            // Session JWTs issued before this migration carry no stamp and stop being accepted.
            migrationBuilder.Sql("""
                ALTER TABLE users
                    ADD COLUMN security_stamp character varying(32) NOT NULL
                    DEFAULT replace(gen_random_uuid()::text, '-', '');
                ALTER TABLE users ALTER COLUMN security_stamp DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_users_password_reset_token_hash",
                table: "users",
                column: "password_reset_token_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_password_reset_token_hash",
                table: "users");

            migrationBuilder.DropColumn(
                name: "password_reset_expires_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "password_reset_token_hash",
                table: "users");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                table: "users");
        }
    }
}
