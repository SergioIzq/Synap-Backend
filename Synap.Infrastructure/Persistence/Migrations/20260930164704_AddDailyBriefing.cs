using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyBriefing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "briefing_enabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "briefing_hour",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "briefing_last_resolved_on",
                table: "users",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_briefing_enabled",
                table: "users",
                column: "briefing_enabled",
                filter: "briefing_enabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_briefing_enabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "briefing_enabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "briefing_hour",
                table: "users");

            migrationBuilder.DropColumn(
                name: "briefing_last_resolved_on",
                table: "users");
        }
    }
}
