using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequirementsBenefits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Benefits",
                schema: "jobs",
                table: "applications",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Requirements",
                schema: "jobs",
                table: "applications",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Benefits",
                schema: "jobs",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "Requirements",
                schema: "jobs",
                table: "applications");
        }
    }
}
