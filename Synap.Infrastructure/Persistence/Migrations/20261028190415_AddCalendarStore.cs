using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendars",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    href = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    server_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sync_token = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ctag = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    last_synced_at = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    last_sync_error = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendars", x => x.id);
                    table.ForeignKey(
                        name: "FK_calendars_calendar_connections_user_id",
                        column: x => x.user_id,
                        principalTable: "calendar_connections",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "calendar_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    href = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    etag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    uid = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ics = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    starts_at_utc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    is_recurring = table.Column<bool>(type: "boolean", nullable: false),
                    all_day = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_calendar_events_calendars_calendar_id",
                        column: x => x.calendar_id,
                        principalTable: "calendars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_calendar_events_user_starts",
                table: "calendar_events",
                columns: new[] { "user_id", "starts_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_calendar_events_calendar_href",
                table: "calendar_events",
                columns: new[] { "calendar_id", "href" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_calendars_user_href",
                table: "calendars",
                columns: new[] { "user_id", "href" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_events");

            migrationBuilder.DropTable(
                name: "calendars");
        }
    }
}
