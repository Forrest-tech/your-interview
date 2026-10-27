using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLogtoUserMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "logto_user_mappings",
                schema: "identity",
                columns: table => new
                {
                    LogtoUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LegacyUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logto_user_mappings", x => x.LogtoUserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_logto_user_mappings_LegacyUserId",
                schema: "identity",
                table: "logto_user_mappings",
                column: "LegacyUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "logto_user_mappings",
                schema: "identity");
        }
    }
}
