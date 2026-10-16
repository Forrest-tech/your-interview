using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybookRedesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mastery",
                schema: "interviews",
                table: "questions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "QuestionsToAskJson",
                schema: "interviews",
                table: "entries",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "actions",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Detail = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    DueDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDone = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_actions_entries_InterviewEntryId",
                        column: x => x.InterviewEntryId,
                        principalSchema: "interviews",
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "analysis_snapshots",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    OverallScore = table.Column<int>(type: "integer", nullable: true),
                    DimensionsJson = table.Column<string>(type: "text", nullable: true),
                    SummaryBulletsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_analysis_snapshots_entries_InterviewEntryId",
                        column: x => x.InterviewEntryId,
                        principalSchema: "interviews",
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_actions_InterviewEntryId",
                schema: "interviews",
                table: "actions",
                column: "InterviewEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_snapshots_InterviewEntryId",
                schema: "interviews",
                table: "analysis_snapshots",
                column: "InterviewEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_snapshots_InterviewEntryId_CreatedAt",
                schema: "interviews",
                table: "analysis_snapshots",
                columns: new[] { "InterviewEntryId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actions",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "analysis_snapshots",
                schema: "interviews");

            migrationBuilder.DropColumn(
                name: "Mastery",
                schema: "interviews",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "QuestionsToAskJson",
                schema: "interviews",
                table: "entries");
        }
    }
}
