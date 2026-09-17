using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScoreBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BilledBytes",
                schema: "assessment",
                table: "practice_recording_scores",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BilledSeconds",
                schema: "assessment",
                table: "practice_recording_scores",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BilledBytes",
                schema: "assessment",
                table: "practice_recording_scores");

            migrationBuilder.DropColumn(
                name: "BilledSeconds",
                schema: "assessment",
                table: "practice_recording_scores");
        }
    }
}
