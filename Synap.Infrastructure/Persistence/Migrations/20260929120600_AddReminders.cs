using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "telegram_chat_id",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "telegram_link_token",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "telegram_link_token_expires",
                table: "users",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "timezone",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reminders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    due_at = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recurrence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    dismissed_at = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminders", x => x.id);
                    table.ForeignKey(
                        name: "FK_reminders_notes_note_id",
                        column: x => x.note_id,
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_reminders_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_telegram_chat_id",
                table: "users",
                column: "telegram_chat_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_telegram_link_token",
                table: "users",
                column: "telegram_link_token");

            migrationBuilder.CreateIndex(
                name: "idx_reminders_due_pending",
                table: "reminders",
                column: "due_at",
                filter: "sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_reminders_user_id",
                table: "reminders",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reminders_note_id",
                table: "reminders",
                column: "note_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminders");

            migrationBuilder.DropIndex(
                name: "IX_users_telegram_chat_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_telegram_link_token",
                table: "users");

            migrationBuilder.DropColumn(
                name: "telegram_chat_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "telegram_link_token",
                table: "users");

            migrationBuilder.DropColumn(
                name: "telegram_link_token_expires",
                table: "users");

            migrationBuilder.DropColumn(
                name: "timezone",
                table: "users");
        }
    }
}
