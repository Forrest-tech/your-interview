using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Analytics.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "ability_snapshots",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Dimension = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ability_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mastery_breakdowns",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Mastered = table.Column<int>(type: "integer", nullable: false),
                    Learning = table.Column<int>(type: "integer", nullable: false),
                    Fresh = table.Column<int>(type: "integer", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mastery_breakdowns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_snapshots",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Saved = table.Column<int>(type: "integer", nullable: false),
                    Applied = table.Column<int>(type: "integer", nullable: false),
                    Screening = table.Column<int>(type: "integer", nullable: false),
                    Interviewing = table.Column<int>(type: "integer", nullable: false),
                    Offered = table.Column<int>(type: "integer", nullable: false),
                    Rejected = table.Column<int>(type: "integer", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipeline_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "processed_events",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ability_snapshots_UserId_Date_Dimension_Source",
                schema: "analytics",
                table: "ability_snapshots",
                columns: new[] { "UserId", "Date", "Dimension", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mastery_breakdowns_UserId_Date_Topic",
                schema: "analytics",
                table: "mastery_breakdowns",
                columns: new[] { "UserId", "Date", "Topic" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_snapshots_UserId_Date",
                schema: "analytics",
                table: "pipeline_snapshots",
                columns: new[] { "UserId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processed_events_EventId",
                schema: "analytics",
                table: "processed_events",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processed_events_ProcessedAt",
                schema: "analytics",
                table: "processed_events",
                column: "ProcessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ability_snapshots",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "mastery_breakdowns",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "pipeline_snapshots",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "processed_events",
                schema: "analytics");
        }
    }
}
