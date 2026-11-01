using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxWholeMailboxCatchUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWholeMailboxCatchUp",
                schema: "interviews",
                table: "mailbox_backfill_progress",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "mailbox_catch_up_markers",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ScopeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_catch_up_markers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_catch_up_markers_UserId",
                schema: "interviews",
                table: "mailbox_catch_up_markers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_catch_up_markers_UserId_Provider_AccountEmail",
                schema: "interviews",
                table: "mailbox_catch_up_markers",
                columns: new[] { "UserId", "Provider", "AccountEmail" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mailbox_catch_up_markers",
                schema: "interviews");

            migrationBuilder.DropColumn(
                name: "IsWholeMailboxCatchUp",
                schema: "interviews",
                table: "mailbox_backfill_progress");
        }
    }
}
