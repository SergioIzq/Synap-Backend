using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveGoogleCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM calendar_connections WHERE provider = 'Google';");

            migrationBuilder.DropColumn(
                name: "calendar_oauth_expires_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "calendar_oauth_nonce",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "calendar_oauth_expires_at",
                table: "users",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "calendar_oauth_nonce",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
