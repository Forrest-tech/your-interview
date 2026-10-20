using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResumeFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resume_files",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    TextContent = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resume_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_resume_files_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "jobs",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_resume_files_ApplicationId",
                schema: "jobs",
                table: "resume_files",
                column: "ApplicationId",
                unique: true,
                filter: "\"ApplicationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_resume_files_UserId",
                schema: "jobs",
                table: "resume_files",
                column: "UserId",
                unique: true,
                filter: "\"ApplicationId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resume_files",
                schema: "jobs");
        }
    }
}
