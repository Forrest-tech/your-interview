using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "assessment");

            migrationBuilder.CreateTable(
                name: "mock_sessions",
                schema: "assessment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CompanyStyle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TargetQuestionCount = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OverallSummary = table.Column<string>(type: "text", nullable: true),
                    PriorityAction = table.Column<string>(type: "text", nullable: true),
                    OverallScore = table.Column<int>(type: "integer", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mock_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mock_questions",
                schema: "assessment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    QuestionText = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExpectedPointsJson = table.Column<string>(type: "jsonb", nullable: true),
                    Difficulty = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AskedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnswerText = table.Column<string>(type: "text", nullable: true),
                    AnswerAudioPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AnswerDurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    AnswerTranscriptJson = table.Column<string>(type: "jsonb", nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    RecommendedAnswer = table.Column<string>(type: "text", nullable: true),
                    BetterStructure = table.Column<string>(type: "text", nullable: true),
                    FillerWordsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ScoredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    score_pronunciation = table.Column<int>(type: "integer", nullable: true),
                    score_fluency = table.Column<int>(type: "integer", nullable: true),
                    score_sentence_integrity = table.Column<int>(type: "integer", nullable: true),
                    score_structure = table.Column<int>(type: "integer", nullable: true),
                    score_technical_depth = table.Column<int>(type: "integer", nullable: true),
                    score_relevance = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mock_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mock_questions_mock_sessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "assessment",
                        principalTable: "mock_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dimension_issues",
                schema: "assessment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Dimension = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    Evidence = table.Column<string>(type: "text", nullable: true),
                    Suggestion = table.Column<string>(type: "text", nullable: true),
                    Severity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dimension_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dimension_issues_mock_questions_QuestionId",
                        column: x => x.QuestionId,
                        principalSchema: "assessment",
                        principalTable: "mock_questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dimension_issues_Dimension",
                schema: "assessment",
                table: "dimension_issues",
                column: "Dimension");

            migrationBuilder.CreateIndex(
                name: "IX_dimension_issues_QuestionId",
                schema: "assessment",
                table: "dimension_issues",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_mock_questions_SessionId_Sequence",
                schema: "assessment",
                table: "mock_questions",
                columns: new[] { "SessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_mock_sessions_Status",
                schema: "assessment",
                table: "mock_sessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_mock_sessions_UserId_StartedAt",
                schema: "assessment",
                table: "mock_sessions",
                columns: new[] { "UserId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dimension_issues",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "mock_questions",
                schema: "assessment");

            migrationBuilder.DropTable(
                name: "mock_sessions",
                schema: "assessment");
        }
    }
}
