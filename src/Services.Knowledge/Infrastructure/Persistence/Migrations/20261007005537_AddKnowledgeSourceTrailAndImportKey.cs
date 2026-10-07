using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeSourceTrailAndImportKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportKey",
                schema: "knowledge",
                table: "knowledge_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceJobApplicationId",
                schema: "knowledge",
                table: "knowledge_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceRoundNo",
                schema: "knowledge",
                table: "knowledge_items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceRoundStage",
                schema: "knowledge",
                table: "knowledge_items",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_ImportKey",
                schema: "knowledge",
                table: "knowledge_items",
                column: "ImportKey");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_SourceCompanyName",
                schema: "knowledge",
                table: "knowledge_items",
                column: "SourceCompanyName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_items_ImportKey",
                schema: "knowledge",
                table: "knowledge_items");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_items_SourceCompanyName",
                schema: "knowledge",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "ImportKey",
                schema: "knowledge",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "SourceJobApplicationId",
                schema: "knowledge",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "SourceRoundNo",
                schema: "knowledge",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "SourceRoundStage",
                schema: "knowledge",
                table: "knowledge_items");
        }
    }
}
