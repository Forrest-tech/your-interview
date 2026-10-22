using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIdOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "companies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "application_communications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_companies_UserId",
                schema: "jobs",
                table: "companies",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_UserId",
                schema: "jobs",
                table: "applications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_application_communications_UserId",
                schema: "jobs",
                table: "application_communications",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_companies_UserId",
                schema: "jobs",
                table: "companies");

            migrationBuilder.DropIndex(
                name: "IX_applications_UserId",
                schema: "jobs",
                table: "applications");

            migrationBuilder.DropIndex(
                name: "IX_application_communications_UserId",
                schema: "jobs",
                table: "application_communications");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "jobs",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "jobs",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "jobs",
                table: "application_communications");
        }
    }
}
