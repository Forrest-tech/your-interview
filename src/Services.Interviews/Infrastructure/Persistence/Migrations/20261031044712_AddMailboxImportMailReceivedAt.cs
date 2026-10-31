using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxImportMailReceivedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MailReceivedAt",
                schema: "interviews",
                table: "mailbox_import_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_import_records_UserId_EntryId_MailReceivedAt",
                schema: "interviews",
                table: "mailbox_import_records",
                columns: new[] { "UserId", "EntryId", "MailReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mailbox_import_records_UserId_EntryId_MailReceivedAt",
                schema: "interviews",
                table: "mailbox_import_records");

            migrationBuilder.DropColumn(
                name: "MailReceivedAt",
                schema: "interviews",
                table: "mailbox_import_records");
        }
    }
}
