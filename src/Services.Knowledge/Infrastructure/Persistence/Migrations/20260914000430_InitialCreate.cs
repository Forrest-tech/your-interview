using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "knowledge");

            migrationBuilder.CreateTable(
                name: "knowledge_items",
                schema: "knowledge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Topic = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubTopic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Question = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceInterviewEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceCompanyName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Difficulty = table.Column<int>(type: "integer", nullable: false),
                    Importance = table.Column<int>(type: "integer", nullable: false),
                    TagsJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Mastery = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReviewCount = table.Column<int>(type: "integer", nullable: false),
                    LastReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextReviewAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EasinessFactor = table.Column<double>(type: "double precision", nullable: false),
                    RepetitionStreak = table.Column<int>(type: "integer", nullable: false),
                    ConceptExplanation = table.Column<string>(type: "text", nullable: true),
                    MyAnswer = table.Column<string>(type: "text", nullable: true),
                    BetterAnswer = table.Column<string>(type: "text", nullable: true),
                    KeyPointsJson = table.Column<string>(type: "text", nullable: true),
                    CommonMistakesJson = table.Column<string>(type: "text", nullable: true),
                    FollowUpsJson = table.Column<string>(type: "text", nullable: true),
                    ReferencesJson = table.Column<string>(type: "text", nullable: true),
                    LastIntervalDays = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_relations",
                schema: "knowledge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelatedItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_relations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_relations_knowledge_items_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "knowledge",
                        principalTable: "knowledge_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_review_logs",
                schema: "knowledge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConfidenceBefore = table.Column<int>(type: "integer", nullable: true),
                    ConfidenceAfter = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_review_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_review_logs_knowledge_items_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "knowledge",
                        principalTable: "knowledge_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_Mastery",
                schema: "knowledge",
                table: "knowledge_items",
                column: "Mastery");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_Mastery_NextReviewAt",
                schema: "knowledge",
                table: "knowledge_items",
                columns: new[] { "Mastery", "NextReviewAt" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_NextReviewAt",
                schema: "knowledge",
                table: "knowledge_items",
                column: "NextReviewAt");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_Source",
                schema: "knowledge",
                table: "knowledge_items",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_SourceInterviewEntryId",
                schema: "knowledge",
                table: "knowledge_items",
                column: "SourceInterviewEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_Topic",
                schema: "knowledge",
                table: "knowledge_items",
                column: "Topic");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_relations_ItemId",
                schema: "knowledge",
                table: "knowledge_relations",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_relations_ItemId_RelatedItemId_RelationType",
                schema: "knowledge",
                table: "knowledge_relations",
                columns: new[] { "ItemId", "RelatedItemId", "RelationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_relations_RelatedItemId",
                schema: "knowledge",
                table: "knowledge_relations",
                column: "RelatedItemId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_review_logs_ItemId",
                schema: "knowledge",
                table: "knowledge_review_logs",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_review_logs_ItemId_ReviewedAt",
                schema: "knowledge",
                table: "knowledge_review_logs",
                columns: new[] { "ItemId", "ReviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_review_logs_ReviewedAt",
                schema: "knowledge",
                table: "knowledge_review_logs",
                column: "ReviewedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_relations",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "knowledge_review_logs",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "knowledge_items",
                schema: "knowledge");
        }
    }
}
