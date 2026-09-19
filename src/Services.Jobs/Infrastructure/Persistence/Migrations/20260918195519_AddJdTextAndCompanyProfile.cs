using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJdTextAndCompanyProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Profile",
                schema: "jobs",
                table: "companies",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileSourcesJson",
                schema: "jobs",
                table: "companies",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JdSourceUrl",
                schema: "jobs",
                table: "applications",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JdText",
                schema: "jobs",
                table: "applications",
                type: "character varying(40000)",
                maxLength: 40000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Profile",
                schema: "jobs",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "ProfileSourcesJson",
                schema: "jobs",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "JdSourceUrl",
                schema: "jobs",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "JdText",
                schema: "jobs",
                table: "applications");
        }
    }
}
