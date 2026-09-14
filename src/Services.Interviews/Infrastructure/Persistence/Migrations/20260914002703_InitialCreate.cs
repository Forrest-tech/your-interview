using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "interviews");

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    JobApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CompanyProfile = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    JdText = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: true),
                    JdSummary = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    RoundNo = table.Column<int>(type: "integer", nullable: false),
                    InterviewDate = table.Column<DateOnly>(type: "date", nullable: true),
                    InterviewFormat = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Interviewers = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Result = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TranscribedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AnalyzedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OverallScore = table.Column<int>(type: "integer", nullable: true),
                    PronunciationScore = table.Column<int>(type: "integer", nullable: true),
                    FluencyScore = table.Column<int>(type: "integer", nullable: true),
                    StructureScore = table.Column<int>(type: "integer", nullable: true),
                    TechnicalDepthScore = table.Column<int>(type: "integer", nullable: true),
                    RelevanceScore = table.Column<int>(type: "integer", nullable: true),
                    AnalysisSummary = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    BlobUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    SourceLanguage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TranscriptText = table.Column<string>(type: "character varying(500000)", maxLength: 500000, nullable: true),
                    TranscriptSegmentsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assets_entries_InterviewEntryId",
                        column: x => x.InterviewEntryId,
                        principalSchema: "interviews",
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "questions",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    QuestionText = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    MyAnswerText = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: true),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Difficulty = table.Column<int>(type: "integer", nullable: false),
                    Assessment = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    GotStuck = table.Column<bool>(type: "boolean", nullable: false),
                    StuckReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RecommendedAnswer = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    AskedAtSeconds = table.Column<double>(type: "double precision", nullable: true),
                    WeaknessTagsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FollowUpQuestionsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    MissedPointsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_questions_entries_InterviewEntryId",
                        column: x => x.InterviewEntryId,
                        principalSchema: "interviews",
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "weaknesses",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Detail = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Evidence = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Suggestion = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weaknesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_weaknesses_entries_InterviewEntryId",
                        column: x => x.InterviewEntryId,
                        principalSchema: "interviews",
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assets_InterviewEntryId",
                schema: "interviews",
                table: "assets",
                column: "InterviewEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_entries_CompanyId",
                schema: "interviews",
                table: "entries",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_entries_CompanyName",
                schema: "interviews",
                table: "entries",
                column: "CompanyName");

            migrationBuilder.CreateIndex(
                name: "IX_entries_InterviewDate",
                schema: "interviews",
                table: "entries",
                column: "InterviewDate");

            migrationBuilder.CreateIndex(
                name: "IX_entries_Status",
                schema: "interviews",
                table: "entries",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_questions_InterviewEntryId",
                schema: "interviews",
                table: "questions",
                column: "InterviewEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_questions_InterviewEntryId_Sequence",
                schema: "interviews",
                table: "questions",
                columns: new[] { "InterviewEntryId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_weaknesses_Category",
                schema: "interviews",
                table: "weaknesses",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_weaknesses_InterviewEntryId",
                schema: "interviews",
                table: "weaknesses",
                column: "InterviewEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assets",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "questions",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "weaknesses",
                schema: "interviews");

            migrationBuilder.DropTable(
                name: "entries",
                schema: "interviews");
        }
    }
}
