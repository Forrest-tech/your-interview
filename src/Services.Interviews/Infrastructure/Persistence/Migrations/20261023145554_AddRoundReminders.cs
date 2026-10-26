using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoundReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "interviews",
                table: "rounds",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "interviews",
                table: "questions",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "interviews",
                table: "assets",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "round_reminders",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RemindAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_round_reminders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_round_reminders_EntryId",
                schema: "interviews",
                table: "round_reminders",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_round_reminders_RoundId_Kind",
                schema: "interviews",
                table: "round_reminders",
                columns: new[] { "RoundId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_round_reminders_UserId",
                schema: "interviews",
                table: "round_reminders",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_round_reminders_UserId_Status_RemindAt",
                schema: "interviews",
                table: "round_reminders",
                columns: new[] { "UserId", "Status", "RemindAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "round_reminders",
                schema: "interviews");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "interviews",
                table: "rounds");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "interviews",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "interviews",
                table: "assets");
        }
    }
}
