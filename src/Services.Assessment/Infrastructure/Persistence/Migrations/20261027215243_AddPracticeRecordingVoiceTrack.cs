using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeRecordingVoiceTrack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "LaneShiftSec",
                schema: "assessment",
                table: "practice_recordings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TakeRate",
                schema: "assessment",
                table: "practice_recordings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoiceContentType",
                schema: "assessment",
                table: "practice_recordings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VoiceSizeBytes",
                schema: "assessment",
                table: "practice_recordings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoiceStoragePath",
                schema: "assessment",
                table: "practice_recordings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LaneShiftSec",
                schema: "assessment",
                table: "practice_recordings");

            migrationBuilder.DropColumn(
                name: "TakeRate",
                schema: "assessment",
                table: "practice_recordings");

            migrationBuilder.DropColumn(
                name: "VoiceContentType",
                schema: "assessment",
                table: "practice_recordings");

            migrationBuilder.DropColumn(
                name: "VoiceSizeBytes",
                schema: "assessment",
                table: "practice_recordings");

            migrationBuilder.DropColumn(
                name: "VoiceStoragePath",
                schema: "assessment",
                table: "practice_recordings");
        }
    }
}
