using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIdOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                schema: "knowledge",
                table: "knowledge_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_UserId",
                schema: "knowledge",
                table: "knowledge_items",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_items_UserId",
                schema: "knowledge",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "knowledge",
                table: "knowledge_items");
        }
    }
}
