using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybookRedesignV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrepReportJson",
                schema: "interviews",
                table: "rounds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewJson",
                schema: "interviews",
                table: "rounds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AskedBy",
                schema: "interviews",
                table: "questions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewJson",
                schema: "interviews",
                table: "questions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoundId",
                schema: "interviews",
                table: "questions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaJson",
                schema: "interviews",
                table: "assets",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrepReportJson",
                schema: "interviews",
                table: "rounds");

            migrationBuilder.DropColumn(
                name: "ReviewJson",
                schema: "interviews",
                table: "rounds");

            migrationBuilder.DropColumn(
                name: "AskedBy",
                schema: "interviews",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "ReviewJson",
                schema: "interviews",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "RoundId",
                schema: "interviews",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "MetaJson",
                schema: "interviews",
                table: "assets");
        }
    }
}
