using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequireUserIdOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "knowledge",
                table: "knowledge_items",
                type: "uuid",
                nullable: false,
                // COD-01 (ruling D): defaultValue intentionally removed -- rows still
                // NULL after backfill must fail this contract step loudly instead of
                // being silently stamped Guid.Empty.
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "knowledge",
                table: "knowledge_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
