using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetRoundPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Phase",
                schema: "interviews",
                table: "assets",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoundId",
                schema: "interviews",
                table: "assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_InterviewEntryId_RoundId",
                schema: "interviews",
                table: "assets",
                columns: new[] { "InterviewEntryId", "RoundId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assets_InterviewEntryId_RoundId",
                schema: "interviews",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "Phase",
                schema: "interviews",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "RoundId",
                schema: "interviews",
                table: "assets");
        }
    }
}
