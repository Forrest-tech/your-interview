using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxBackfillProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mailbox_backfill_progress",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ScopeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    WindowStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartHistoryId = table.Column<string>(type: "text", nullable: true),
                    CurrentPageIdsJson = table.Column<string>(type: "text", nullable: false),
                    CurrentPageIndex = table.Column<int>(type: "integer", nullable: false),
                    NextPageToken = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_backfill_progress", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_backfill_progress_UserId",
                schema: "interviews",
                table: "mailbox_backfill_progress",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_backfill_progress_UserId_Provider_AccountEmail",
                schema: "interviews",
                table: "mailbox_backfill_progress",
                columns: new[] { "UserId", "Provider", "AccountEmail" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mailbox_backfill_progress",
                schema: "interviews");
        }
    }
}
