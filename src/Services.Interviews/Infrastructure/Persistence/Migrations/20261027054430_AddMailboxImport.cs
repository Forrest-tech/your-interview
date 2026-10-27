using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mail_contact_maps",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderEmailLower = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_contact_maps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_match_rules",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainsJson = table.Column<string>(type: "text", nullable: false),
                    ContactsJson = table.Column<string>(type: "text", nullable: false),
                    KeywordsJson = table.Column<string>(type: "text", nullable: false),
                    ExclusionsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_match_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mailbox_connections",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Scope = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EncryptedRefreshToken = table.Column<string>(type: "text", nullable: false),
                    Cursor = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRefreshAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mailbox_import_records",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ProviderThreadId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MatchedRule = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RawMimeSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RawMimeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CursorSnapshot = table.Column<string>(type: "text", nullable: true),
                    IsNew = table.Column<bool>(type: "boolean", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_import_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mailbox_pending_items",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ProviderThreadId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FromEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    FromName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Subject = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Signal = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MatchedRule = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CandidateEntryIdsJson = table.Column<string>(type: "text", nullable: true),
                    Snippet = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_pending_items", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mail_contact_maps_UserId_SenderEmailLower",
                schema: "interviews",
                table: "mail_contact_maps",
                columns: new[] { "UserId", "SenderEmailLower" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_match_rules_UserId_EntryId",
                schema: "interviews",
                table: "mail_match_rules",
                columns: new[] { "UserId", "EntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_connections_UserId",
                schema: "interviews",
                table: "mailbox_connections",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_connections_UserId_Status",
                schema: "interviews",
                table: "mailbox_connections",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_import_records_AssetId",
                schema: "interviews",
                table: "mailbox_import_records",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_import_records_UserId_EntryId_ImportedAt",
                schema: "interviews",
                table: "mailbox_import_records",
                columns: new[] { "UserId", "EntryId", "ImportedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_import_records_UserId_Provider_AccountEmail_Provide~",
                schema: "interviews",
                table: "mailbox_import_records",
                columns: new[] { "UserId", "Provider", "AccountEmail", "ProviderMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_pending_items_UserId_Provider_ProviderMessageId",
                schema: "interviews",
                table: "mailbox_pending_items",
                columns: new[] { "UserId", "Provider", "ProviderMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_pending_items_UserId_Status_CreatedAt",
                schema: "interviews",
                table: "mailbox_pending_items",
                columns: new[] { "UserId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_contact_maps",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "mail_match_rules",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "mailbox_connections",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "mailbox_import_records",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "mailbox_pending_items",
                schema: "interviews");
        }
    }
}
