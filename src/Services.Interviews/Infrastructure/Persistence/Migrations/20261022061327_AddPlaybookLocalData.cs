using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybookLocalData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legacy_debriefs",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_debriefs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "legacy_pasted_materials",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_pasted_materials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "star_stories",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Situation = table.Column<string>(type: "text", nullable: true),
                    Task = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: true),
                    Result = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_star_stories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_debriefs_UserId",
                schema: "interviews",
                table: "legacy_debriefs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_legacy_debriefs_UserId_EntryId",
                schema: "interviews",
                table: "legacy_debriefs",
                columns: new[] { "UserId", "EntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legacy_pasted_materials_UserId",
                schema: "interviews",
                table: "legacy_pasted_materials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_legacy_pasted_materials_UserId_EntryId",
                schema: "interviews",
                table: "legacy_pasted_materials",
                columns: new[] { "UserId", "EntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_star_stories_UserId",
                schema: "interviews",
                table: "star_stories",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_star_stories_UserId_ClientId",
                schema: "interviews",
                table: "star_stories",
                columns: new[] { "UserId", "ClientId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legacy_debriefs",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "legacy_pasted_materials",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "star_stories",
                schema: "interviews");
        }
    }
}
