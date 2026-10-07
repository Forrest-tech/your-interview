using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInterviewRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rounds",
                schema: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InterviewEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Interviewers = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Format = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    Feedback = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rounds_entries_InterviewEntryId",
                        column: x => x.InterviewEntryId,
                        principalSchema: "interviews",
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rounds_InterviewEntryId",
                schema: "interviews",
                table: "rounds",
                column: "InterviewEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_rounds_InterviewEntryId_Order",
                schema: "interviews",
                table: "rounds",
                columns: new[] { "InterviewEntryId", "Order" },
                unique: true);

            // 数据搬迁:把原来 entries 上的扁平轮次字段搬成第 1 轮,别丢用户已填的时间/面试官/结果。
            migrationBuilder.Sql(@"
                INSERT INTO interviews.rounds
                    (""Id"", ""InterviewEntryId"", ""Order"", ""Stage"", ""ScheduledDate"",
                     ""Interviewers"", ""Format"", ""Location"", ""Outcome"", ""Notes"", ""Feedback"")
                SELECT gen_random_uuid(), ""Id"", 1, 'Technical', ""InterviewDate"",
                       ""Interviewers"", ""InterviewFormat"", ""Location"",
                       CASE WHEN ""Result"" IN ('Passed','Rejected','Ghosted','Cancelled','NoShow')
                            THEN ""Result"" ELSE 'Pending' END,
                       ""Notes"", NULL
                FROM interviews.entries;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rounds",
                schema: "interviews");
        }
    }
}
