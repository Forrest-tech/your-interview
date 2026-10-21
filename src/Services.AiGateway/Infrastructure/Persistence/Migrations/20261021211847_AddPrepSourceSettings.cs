using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.AiGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrepSourceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prep_source_settings",
                schema: "aigateway",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionBankEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CompanyBackgroundEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    WebExperiencesEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    GlassdoorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SearchProvider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SearchApiKey = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prep_source_settings", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prep_source_settings",
                schema: "aigateway");
        }
    }
}
