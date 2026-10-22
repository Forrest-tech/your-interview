using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerFavoritesAndQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "application_favorites",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_favorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_application_favorites_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "jobs",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "application_questions",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_application_questions_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "jobs",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_favorites_ApplicationId",
                schema: "jobs",
                table: "application_favorites",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_application_favorites_UserId",
                schema: "jobs",
                table: "application_favorites",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_application_favorites_UserId_ApplicationId",
                schema: "jobs",
                table: "application_favorites",
                columns: new[] { "UserId", "ApplicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_application_questions_ApplicationId",
                schema: "jobs",
                table: "application_questions",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_application_questions_UserId_ApplicationId",
                schema: "jobs",
                table: "application_questions",
                columns: new[] { "UserId", "ApplicationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_favorites",
                schema: "jobs");

            migrationBuilder.DropTable(
                name: "application_questions",
                schema: "jobs");
        }
    }
}
