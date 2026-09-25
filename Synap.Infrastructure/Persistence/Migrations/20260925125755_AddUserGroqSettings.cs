using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserGroqSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "groq_api_key_encrypted",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "groq_api_key_last4",
                table: "users",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "groq_api_key_updated_at",
                table: "users",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "groq_model",
                table: "users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "groq_api_key_encrypted",
                table: "users");

            migrationBuilder.DropColumn(
                name: "groq_api_key_last4",
                table: "users");

            migrationBuilder.DropColumn(
                name: "groq_api_key_updated_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "groq_model",
                table: "users");
        }
    }
}
