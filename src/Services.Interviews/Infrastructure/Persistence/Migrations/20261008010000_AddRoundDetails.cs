using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoundDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MeetingLink",
                schema: "interviews",
                table: "rounds",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduledTime",
                schema: "interviews",
                table: "rounds",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrepQuestionsJson",
                schema: "interviews",
                table: "rounds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailsJson",
                schema: "interviews",
                table: "rounds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transcript",
                schema: "interviews",
                table: "rounds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecordingUrl",
                schema: "interviews",
                table: "rounds",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MeetingLink", schema: "interviews", table: "rounds");
            migrationBuilder.DropColumn(name: "ScheduledTime", schema: "interviews", table: "rounds");
            migrationBuilder.DropColumn(name: "PrepQuestionsJson", schema: "interviews", table: "rounds");
            migrationBuilder.DropColumn(name: "EmailsJson", schema: "interviews", table: "rounds");
            migrationBuilder.DropColumn(name: "Transcript", schema: "interviews", table: "rounds");
            migrationBuilder.DropColumn(name: "RecordingUrl", schema: "interviews", table: "rounds");
        }
    }
}
