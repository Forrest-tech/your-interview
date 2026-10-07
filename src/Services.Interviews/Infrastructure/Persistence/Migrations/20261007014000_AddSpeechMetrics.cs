using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeechMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpeechMetricsJson",
                schema: "interviews",
                table: "entries",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpeechMetricsJson",
                schema: "interviews",
                table: "entries");
        }
    }
}
