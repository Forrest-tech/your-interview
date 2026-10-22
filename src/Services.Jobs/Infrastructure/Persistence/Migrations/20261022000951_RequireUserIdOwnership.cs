using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequireUserIdOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // COD-01 contract:UserId 收紧为 NOT NULL。不给 defaultValue ——
            // 若 backfill 守卫跳过导致残余 NULL,本迁移应响亮失败(裁决 D),而不是静默填 Guid.Empty。
            migrationBuilder.DropIndex(
                name: "IX_companies_Name",
                schema: "jobs",
                table: "companies");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "companies",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "applications",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "application_communications",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_companies_Name",
                schema: "jobs",
                table: "companies",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_companies_UserId_Name",
                schema: "jobs",
                table: "companies",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_companies_Name",
                schema: "jobs",
                table: "companies");

            migrationBuilder.DropIndex(
                name: "IX_companies_UserId_Name",
                schema: "jobs",
                table: "companies");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "companies",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "applications",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "jobs",
                table: "application_communications",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_companies_Name",
                schema: "jobs",
                table: "companies",
                column: "Name",
                unique: true);
        }
    }
}
