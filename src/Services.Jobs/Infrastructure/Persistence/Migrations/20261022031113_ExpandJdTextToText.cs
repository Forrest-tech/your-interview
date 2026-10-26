using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpandJdTextToText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "JdText",
                schema: "jobs",
                table: "applications",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(40000)",
                oldMaxLength: 40000,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "JdText",
                schema: "jobs",
                table: "applications",
                type: "character varying(40000)",
                maxLength: 40000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
