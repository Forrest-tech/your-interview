using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeRecordingSentenceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSentence",
                schema: "assessment",
                table: "practice_recordings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SentenceText",
                schema: "assessment",
                table: "practice_recordings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSentence",
                schema: "assessment",
                table: "practice_recordings");

            migrationBuilder.DropColumn(
                name: "SentenceText",
                schema: "assessment",
                table: "practice_recordings");
        }
    }
}
