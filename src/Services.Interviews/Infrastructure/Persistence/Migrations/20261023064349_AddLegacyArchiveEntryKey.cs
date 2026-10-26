using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacyArchiveEntryKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_legacy_pasted_materials_UserId_EntryId",
                schema: "interviews",
                table: "legacy_pasted_materials");

            migrationBuilder.DropIndex(
                name: "IX_legacy_debriefs_UserId_EntryId",
                schema: "interviews",
                table: "legacy_debriefs");

            migrationBuilder.AddColumn<string>(
                name: "EntryKey",
                schema: "interviews",
                table: "legacy_pasted_materials",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EntryKey",
                schema: "interviews",
                table: "legacy_debriefs",
                type: "text",
                nullable: false,
                defaultValue: "");

            // 票77 回填:既有行都是票15 按真实条目 Guid 归档的,EntryKey =
            // EntryId 的规范文本形(::text 即 Guid 规范串,与新口径归一一致);
            // 必须在建 (UserId, EntryKey) 唯一索引前完成,否则空串撞唯一约束。
            migrationBuilder.Sql(
                "UPDATE interviews.legacy_pasted_materials SET \"EntryKey\" = \"EntryId\"::text;");
            migrationBuilder.Sql(
                "UPDATE interviews.legacy_debriefs SET \"EntryKey\" = \"EntryId\"::text;");

            migrationBuilder.CreateIndex(
                name: "IX_legacy_pasted_materials_UserId_EntryKey",
                schema: "interviews",
                table: "legacy_pasted_materials",
                columns: new[] { "UserId", "EntryKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legacy_debriefs_UserId_EntryKey",
                schema: "interviews",
                table: "legacy_debriefs",
                columns: new[] { "UserId", "EntryKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_legacy_pasted_materials_UserId_EntryKey",
                schema: "interviews",
                table: "legacy_pasted_materials");

            migrationBuilder.DropIndex(
                name: "IX_legacy_debriefs_UserId_EntryKey",
                schema: "interviews",
                table: "legacy_debriefs");

            migrationBuilder.DropColumn(
                name: "EntryKey",
                schema: "interviews",
                table: "legacy_pasted_materials");

            migrationBuilder.DropColumn(
                name: "EntryKey",
                schema: "interviews",
                table: "legacy_debriefs");

            migrationBuilder.CreateIndex(
                name: "IX_legacy_pasted_materials_UserId_EntryId",
                schema: "interviews",
                table: "legacy_pasted_materials",
                columns: new[] { "UserId", "EntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legacy_debriefs_UserId_EntryId",
                schema: "interviews",
                table: "legacy_debriefs",
                columns: new[] { "UserId", "EntryId" },
                unique: true);
        }
    }
}
