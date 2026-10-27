using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLogtoUserMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "logto_user_mapping",
                schema: "identity",
                columns: table => new
                {
                    LogtoUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LegacyUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Approval = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsSuspended = table.Column<bool>(type: "boolean", nullable: false),
                    SuspendedByGate = table.Column<bool>(type: "boolean", nullable: false),
                    LastEventAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logto_user_mapping", x => x.LogtoUserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_logto_user_mapping_LegacyUserId",
                schema: "identity",
                table: "logto_user_mapping",
                column: "LegacyUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "logto_user_mapping",
                schema: "identity");
        }
    }
}
