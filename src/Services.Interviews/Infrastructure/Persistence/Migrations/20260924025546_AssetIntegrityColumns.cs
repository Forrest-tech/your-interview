using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssetIntegrityColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FileExists",
                schema: "interviews",
                table: "assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastVerifiedAt",
                schema: "interviews",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sha256",
                schema: "interviews",
                table: "assets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_StoragePath",
                schema: "interviews",
                table: "assets",
                column: "StoragePath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assets_StoragePath",
                schema: "interviews",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "FileExists",
                schema: "interviews",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "LastVerifiedAt",
                schema: "interviews",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "Sha256",
                schema: "interviews",
                table: "assets");
        }
    }
}
