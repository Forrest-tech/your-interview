using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuidanceMaterials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guidance_materials",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ContentMarkdown = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guidance_materials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guidance_materials_entries_InterviewEntryId",
                        column: x => x.InterviewEntryId,
                        principalSchema: "interviews",
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_guidance_materials_InterviewEntryId",
                schema: "interviews",
                table: "guidance_materials",
                column: "InterviewEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_guidance_materials_InterviewEntryId_Version",
                schema: "interviews",
                table: "guidance_materials",
                columns: new[] { "InterviewEntryId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guidance_materials",
                schema: "interviews");
        }
    }
}
